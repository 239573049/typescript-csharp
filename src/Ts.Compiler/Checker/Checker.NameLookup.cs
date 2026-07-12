// Ported from tsgo internal/checker/checker.go — core name-resolution family.
//
// PORT-NOTE: the resolveName trunk itself lives in the BINDER package (Ts.Binder.NameResolver.Resolve,
// nameresolver.go). The checker wires it up in Checker.Construction.cs (CreateNameResolver, checker.go
// :1466-1497) as the `resolveName` field delegate, populating the NameResolver's callback hooks with
// the checker methods ported HERE:
//   - Lookup                            → GetSymbol                            (Checker.Stubs.cs, real)
//   - OnPropertyWithInvalidInitializer  → CheckAndReportErrorForInvalidInitializer (this file)
//   - OnFailedToResolveSymbol           → OnFailedToResolveSymbol              (Checker.Stubs.cs, real)
//   - OnSuccessfullyResolvedSymbol      → OnSuccessfullyResolvedSymbol          (this file)
// and (for createNameResolverForSuggestion) GetSuggestionForSymbolNameLookup (this file).
//
// This file lands the checker-side callbacks + the onFailedToResolveSymbol diagnostic-refinement
// helpers (checkAndReportErrorForMissingPrefix / *ForUsingTypeAsNamespace / *ForExportingPrimitiveType /
// *ForUsingNamespaceAsTypeOrValue / *ForUsingTypeAsValue / *ForUsingValueAsType / *ForExtendingInterface),
// plus the small free helpers they need (isPrimitiveTypeName, isES2015OrLaterConstructorName,
// maybeMappedType, getEntityNameForExtendingInterface, isTypeReferenceIdentifier, IsInTypeQuery).
//
// The `ResolveName` wrapper method (used by CheckVarDeclaredNamesNotShadowed) simply forwards to the
// wired `resolveName` field delegate — same signature as checker.go's `c.resolveName(...)`.
//
// §1.5: diagnostics via `using Diag = ...`; cross-package classes shadowed by sibling namespaces
// (Scanner) are `global::`-qualified where referenced.

using System;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go: the checker exposes name resolution through the `resolveName` field (wired in
    // Checker.Construction.cs). This method wrapper matches the Go `c.resolveName(...)` call form for
    // call sites that reference it as a method (CheckVarDeclaredNamesNotShadowed).
    internal Symbol? ResolveName(Node? location, string name, SymbolFlags meaning, global::Ts.Diagnostics.Message? nameNotFoundMessage, bool isUse, bool excludeGlobals) =>
        resolveName(location, name, meaning, nameNotFoundMessage, isUse, excludeGlobals);

    // checker.go:1514-1530
    // The invalid initializer error is needed in two situation:
    // 1. When result is undefined, after checking for a missing "this."
    // 2. When result is defined
    internal bool CheckAndReportErrorForInvalidInitializer(Node? errorLocation, string name, Node? propertyWithInvalidInitializer, Symbol? result)
    {
        if (!compilerOptions.GetEmitStandardClassFields())
        {
            if (errorLocation != null && result == null && CheckAndReportErrorForMissingPrefix(errorLocation, name))
            {
                return true;
            }
            // We have a match, but the reference occurred within a property initializer and the identifier also binds
            // to a local variable in the constructor where the code will be emitted. Note that this is actually allowed
            // with emitStandardClassFields because the scope semantics are different.
            var prop = propertyWithInvalidInitializer!.AsPropertyDeclaration();
            var message = CoreUtil.IfElse(errorLocation != null && prop.Type != null && prop.Type.Loc.ContainsInclusive(errorLocation.Pos),
                Diag.Type_of_instance_member_variable_0_cannot_reference_identifier_1_declared_in_the_constructor,
                Diag.Initializer_of_instance_member_variable_0_cannot_reference_identifier_1_declared_in_the_constructor);
            Error(errorLocation, message, global::Ts.Scanner.Scanner.DeclarationNameToString(prop.Name()), name);
            return true;
        }
        return false;
    }

    // checker.go:1532-1562
    internal bool CheckAndReportErrorForMissingPrefix(Node errorLocation, string name)
    {
        if (!NodeTests.IsIdentifier(errorLocation) || errorLocation.Text() != name || IsTypeReferenceIdentifier(errorLocation) || IsInTypeQuery(errorLocation))
        {
            return false;
        }
        var container = AstUtilities.GetThisContainer(errorLocation, false /*includeArrowFunctions*/, false /*includeClassComputedPropertyName*/);
        for (var location = container; location.Parent != null; location = location.Parent)
        {
            if (AstUtilities.IsClassLike(location.Parent))
            {
                var classSymbol = GetSymbolOfDeclaration(location.Parent);
                if (classSymbol == null)
                {
                    break;
                }
                // Check to see if a static member exists.
                var constructorType = GetTypeOfSymbol(classSymbol);
                if (GetPropertyOfType(constructorType, name) != null)
                {
                    Error(errorLocation, Diag.Cannot_find_name_0_Did_you_mean_the_static_member_1_0, name, SymbolToString(classSymbol));
                    return true;
                }
                // No static member is present.
                // Check if we're in an instance method and look for a relevant instance member.
                if (location == container && !AstUtilities.IsStatic(location))
                {
                    var instanceType = GetDeclaredTypeOfSymbol(classSymbol).AsInterfaceType()!.thisType;
                    // TODO: GH#18217
                    if (GetPropertyOfType(instanceType!, name) != null)
                    {
                        Error(errorLocation, Diag.Cannot_find_name_0_Did_you_mean_the_instance_member_this_0, name);
                        return true;
                    }
                }
            }
        }
        return false;
    }

    // checker.go:1603-1622
    internal bool CheckAndReportErrorForUsingTypeAsNamespace(Node errorLocation, string name, SymbolFlags meaning)
    {
        if (meaning == SymbolFlags.Namespace)
        {
            var symbol = ResolveSymbol(resolveName(errorLocation, name, SymbolFlags.Type & ~SymbolFlags.Namespace, null /*nameNotFoundMessage*/, false /*isUse*/, false /*excludeGlobals*/));
            if (symbol != null)
            {
                var parent = errorLocation.Parent!;
                if (NodeTests.IsQualifiedName(parent))
                {
                    Debug.Assert(parent.AsQualifiedName().Left == errorLocation, "Should only be resolving left side of qualified name as a namespace");
                    var propName = parent.AsQualifiedName().Right!.Text();
                    var propType = GetPropertyOfType(GetDeclaredTypeOfSymbol(symbol), propName);
                    if (propType != null)
                    {
                        Error(parent, Diag.Cannot_access_0_1_because_0_is_a_type_but_not_a_namespace_Did_you_mean_to_retrieve_the_type_of_the_property_1_in_0_with_0_1, name, propName);
                        return true;
                    }
                }
                Error(errorLocation, Diag.X_0_only_refers_to_a_type_but_is_being_used_as_a_namespace_here, name);
                return true;
            }
        }
        return false;
    }

    // checker.go:1624-1630
    internal bool CheckAndReportErrorForExportingPrimitiveType(Node errorLocation, string name)
    {
        if (IsPrimitiveTypeName(name) && errorLocation.Parent!.Kind == SyntaxKind.ExportSpecifier)
        {
            Error(errorLocation, Diag.Cannot_export_0_Only_local_declarations_can_be_exported_from_a_module, name);
            return true;
        }
        return false;
    }

    // checker.go:1632-1634
    private static bool IsPrimitiveTypeName(string s) =>
        s == "any" || s == "string" || s == "number" || s == "boolean" || s == "never" || s == "unknown";

    // checker.go:1636-1651
    internal bool CheckAndReportErrorForUsingNamespaceAsTypeOrValue(Node errorLocation, string name, SymbolFlags meaning)
    {
        if ((meaning & (SymbolFlags.Value & ~SymbolFlags.Type)) != 0)
        {
            var symbol = ResolveSymbol(resolveName(errorLocation, name, SymbolFlags.NamespaceModule, null /*nameNotFoundMessage*/, false /*isUse*/, false /*excludeGlobals*/));
            if (symbol != null)
            {
                Error(errorLocation, Diag.Cannot_use_namespace_0_as_a_value, name);
                return true;
            }
        }
        else if ((meaning & (SymbolFlags.Type & ~SymbolFlags.Value)) != 0)
        {
            var symbol = ResolveSymbol(resolveName(errorLocation, name, SymbolFlags.Module, null /*nameNotFoundMessage*/, false /*isUse*/, false /*excludeGlobals*/));
            if (symbol != null)
            {
                Error(errorLocation, Diag.Cannot_use_namespace_0_as_a_type, name);
                return true;
            }
        }
        return false;
    }

    // checker.go:1653-1688
    internal bool CheckAndReportErrorForUsingTypeAsValue(Node errorLocation, string name, SymbolFlags meaning)
    {
        if ((meaning & SymbolFlags.Value) != 0)
        {
            if (IsPrimitiveTypeName(name))
            {
                var grandparent = errorLocation.Parent!.Parent;
                if (grandparent != null && grandparent.Parent != null && NodeTests.IsHeritageClause(grandparent))
                {
                    var heritageKind = grandparent.AsHeritageClause().Token;
                    var containerKind = grandparent.Parent.Kind;
                    if (containerKind == SyntaxKind.InterfaceDeclaration && heritageKind == SyntaxKind.ExtendsKeyword)
                    {
                        Error(errorLocation, Diag.An_interface_cannot_extend_a_primitive_type_like_0_It_can_only_extend_other_named_object_types, name);
                    }
                    else if (AstUtilities.IsClassLike(grandparent.Parent) && heritageKind == SyntaxKind.ExtendsKeyword)
                    {
                        Error(errorLocation, Diag.A_class_cannot_extend_a_primitive_type_like_0_Classes_can_only_extend_constructable_values, name);
                    }
                    else if (AstUtilities.IsClassLike(grandparent.Parent) && heritageKind == SyntaxKind.ImplementsKeyword)
                    {
                        Error(errorLocation, Diag.A_class_cannot_implement_a_primitive_type_like_0_It_can_only_implement_other_named_object_types, name);
                    }
                }
                else
                {
                    Error(errorLocation, Diag.X_0_only_refers_to_a_type_but_is_being_used_as_a_value_here, name);
                }
                return true;
            }
            var symbol = ResolveSymbol(resolveName(errorLocation, name, SymbolFlags.Type & ~SymbolFlags.Value, null /*nameNotFoundMessage*/, false /*isUse*/, false /*excludeGlobals*/));
            if (symbol != null)
            {
                var allFlags = GetSymbolFlags(symbol);
                if ((allFlags & SymbolFlags.Value) == 0)
                {
                    if (IsES2015OrLaterConstructorName(name))
                    {
                        Error(errorLocation, Diag.X_0_only_refers_to_a_type_but_is_being_used_as_a_value_here_Do_you_need_to_change_your_target_library_Try_changing_the_lib_compiler_option_to_es2015_or_later, name);
                    }
                    else if (MaybeMappedType(errorLocation, symbol))
                    {
                        Error(errorLocation, Diag.X_0_only_refers_to_a_type_but_is_being_used_as_a_value_here_Did_you_mean_to_use_1_in_0, name, CoreUtil.IfElse(name == "K", "P", "K"));
                    }
                    else
                    {
                        Error(errorLocation, Diag.X_0_only_refers_to_a_type_but_is_being_used_as_a_value_here, name);
                    }
                    return true;
                }
            }
        }
        return false;
    }

    // checker.go:1690-1692
    private static bool IsES2015OrLaterConstructorName(string s) =>
        s == "Promise" || s == "Symbol" || s == "Map" || s == "WeakMap" || s == "Set" || s == "WeakSet";

    // checker.go:1694-1706
    private bool MaybeMappedType(Node node, Symbol symbol)
    {
        while (true)
        {
            node = node.Parent!;
            if (!(NodeTests.IsComputedPropertyName(node) || NodeTests.IsPropertySignatureDeclaration(node)))
            {
                break;
            }
        }
        if (NodeTests.IsTypeLiteralNode(node) && node.Members().Len() == 1)
        {
            var t = GetDeclaredTypeOfSymbol(symbol);
            return (t.flags & TypeFlags.Union) != 0 && AllTypesAssignableToKindEx(t, TypeFlags.StringOrNumberLiteral, true /*strict*/);
        }
        return false;
    }

    // checker.go:1708-1717
    internal bool CheckAndReportErrorForUsingValueAsType(Node errorLocation, string name, SymbolFlags meaning)
    {
        if ((meaning & (SymbolFlags.Type & ~SymbolFlags.Namespace)) != 0)
        {
            var symbol = ResolveSymbol(resolveName(errorLocation, name, ~SymbolFlags.Type & SymbolFlags.Value, null /*nameNotFoundMessage*/, false /*isUse*/, false /*excludeGlobals*/));
            if (symbol != null && (symbol.Flags & SymbolFlags.Namespace) == 0)
            {
                Error(errorLocation, Diag.X_0_refers_to_a_value_but_is_being_used_as_a_type_here_Did_you_mean_typeof_0, name);
                return true;
            }
        }
        return false;
    }

    // checker.go:1761-1771
    internal Symbol? GetSuggestionForSymbolNameLookup(SymbolTable? symbols, string name, SymbolFlags meaning)
    {
        var symbol = GetSymbol(symbols, name, meaning);
        if (symbol != null)
        {
            return symbol;
        }
        System.Collections.Generic.IEnumerable<Symbol>? extras = null;
        if ((meaning & SymbolFlags.GlobalLookup) != 0)
        {
            extras = GetPrimitiveTypeAliasSuggestions(symbols);
        }
        return GetSpellingSuggestionForName(name, ConcatenateSeq(symbols is null ? System.Array.Empty<Symbol>() : symbols.Values, extras), meaning);
    }

    // PORT-NOTE: Go core.ConcatenateSeq(iter.Seq...) — concatenate two possibly-nil sequences. The
    // second (extras) is nil unless GlobalLookup; a nil sequence contributes nothing.
    private static System.Collections.Generic.IEnumerable<Symbol> ConcatenateSeq(System.Collections.Generic.IEnumerable<Symbol> first, System.Collections.Generic.IEnumerable<Symbol>? second)
    {
        foreach (var x in first)
        {
            yield return x;
        }
        if (second != null)
        {
            foreach (var x in second)
            {
                yield return x;
            }
        }
    }

    // checker.go:1806-1872
    internal void OnSuccessfullyResolvedSymbol(Node? errorLocation, Symbol result, SymbolFlags meaning, Node? lastLocation, Node? associatedDeclarationForContainingInitializerOrBindingName, bool withinDeferredContext)
    {
        var name = result.Name;
        var isInExternalModule = lastLocation != null && NodeTests.IsSourceFile(lastLocation) && AstUtilities.IsExternalOrCommonJSModule(lastLocation.AsSourceFile());
        // Only check for block-scoped variable if we have an error location and are looking for the
        // name with variable meaning
        //      For example,
        //          declare module foo {
        //              interface bar {}
        //          }
        //      const foo/*1*/: foo/*2*/.bar;
        // The foo at /*1*/ and /*2*/ will share same symbol with two meanings:
        // block-scoped variable and namespace module. However, only when we
        // try to resolve name in /*1*/ which is used in variable position,
        // we want to check for block-scoped
        if (errorLocation != null && ((meaning & SymbolFlags.BlockScopedVariable) != 0 || (meaning & (SymbolFlags.Class | SymbolFlags.Enum)) != 0 && (meaning & SymbolFlags.Value) == SymbolFlags.Value))
        {
            var exportOrLocalSymbol = GetExportSymbolOfValueSymbolIfExported(result)!;
            if ((exportOrLocalSymbol.Flags & (SymbolFlags.BlockScopedVariable | SymbolFlags.Class | SymbolFlags.Enum)) != 0)
            {
                CheckResolvedBlockScopedVariable(exportOrLocalSymbol, errorLocation);
            }
        }
        // If we're in an external module, we can't reference value symbols created from UMD export declarations
        if (isInExternalModule && (meaning & SymbolFlags.Value) == SymbolFlags.Value && (errorLocation!.Flags & NodeFlags.JSDoc) == 0)
        {
            var merged = GetMergedSymbol(result)!;
            if (merged.Declarations.Len() != 0 && CoreUtil.Every(merged.Declarations!, d =>
                NodeTests.IsNamespaceExportDeclaration(d) || NodeTests.IsSourceFile(d) && d.AsSourceFile()!.GlobalExports != null))
            {
                ErrorOrSuggestion(compilerOptions.AllowUmdGlobalAccess != Tristate.True, errorLocation, Diag.X_0_refers_to_a_UMD_global_but_the_current_file_is_a_module_Consider_adding_an_import_instead, name);
            }
        }
        // If we're in a parameter initializer or binding name, we can't reference the values of the parameter whose initializer we're within or parameters to the right
        if (associatedDeclarationForContainingInitializerOrBindingName != null && !withinDeferredContext && (meaning & SymbolFlags.Value) == SymbolFlags.Value)
        {
            var candidate = GetMergedSymbol(GetLateBoundSymbol(result))!;
            var root = AstUtilities.GetRootDeclaration(associatedDeclarationForContainingInitializerOrBindingName);
            // A parameter initializer or binding pattern initializer within a parameter cannot refer to itself
            if (candidate == GetSymbolOfDeclaration(associatedDeclarationForContainingInitializerOrBindingName))
            {
                Error(errorLocation, Diag.Parameter_0_cannot_reference_itself, global::Ts.Scanner.Scanner.DeclarationNameToString(associatedDeclarationForContainingInitializerOrBindingName.Name()));
            }
            else if (candidate.ValueDeclaration != null && candidate.ValueDeclaration.Pos > associatedDeclarationForContainingInitializerOrBindingName.Pos && root.Parent!.Locals() != null && GetSymbol(root.Parent!.Locals(), candidate.Name, meaning) == candidate)
            {
                Error(errorLocation, Diag.Parameter_0_cannot_reference_identifier_1_declared_after_it, global::Ts.Scanner.Scanner.DeclarationNameToString(associatedDeclarationForContainingInitializerOrBindingName.Name()), global::Ts.Scanner.Scanner.DeclarationNameToString(errorLocation));
            }
        }
        if (errorLocation != null && (meaning & SymbolFlags.Value) != 0 && (result.Flags & SymbolFlags.Alias) != 0 && (result.Flags & SymbolFlags.Value) == 0 && !IsValidTypeOnlyAliasUseSiteCore(errorLocation))
        {
            var typeOnlyDeclaration = GetTypeOnlyAliasDeclarationEx(result, SymbolFlags.Value);
            if (typeOnlyDeclaration != null)
            {
                var message = CoreUtil.IfElse(AstUtilities.NodeKindIs(typeOnlyDeclaration, SyntaxKind.ExportSpecifier, SyntaxKind.ExportDeclaration, SyntaxKind.NamespaceExport),
                    Diag.X_0_cannot_be_used_as_a_value_because_it_was_exported_using_export_type,
                    Diag.X_0_cannot_be_used_as_a_value_because_it_was_imported_using_import_type);
                AddTypeOnlyDeclarationRelatedInfo(Error(errorLocation, message, name), typeOnlyDeclaration, name);
            }
        }
        // Look at 'compilerOptions.isolatedModules' and not 'getIsolatedModules(...)' (which considers 'verbatimModuleSyntax')
        // here because 'verbatimModuleSyntax' will already have an error for importing a type without 'import type'.
        if (compilerOptions.IsolatedModules == Tristate.True && isInExternalModule && (meaning & SymbolFlags.Value) == SymbolFlags.Value)
        {
            var isGlobal = GetSymbol(globals, name, meaning) == result;
            Symbol? nonValueSymbol = null;
            if (isGlobal && NodeTests.IsSourceFile(lastLocation!))
            {
                nonValueSymbol = GetSymbol(lastLocation!.Locals(), name, ~SymbolFlags.Value);
            }
            if (nonValueSymbol != null)
            {
                var importDecl = CoreUtil.Find(nonValueSymbol.Declarations, d =>
                    AstUtilities.NodeKindIs(d, SyntaxKind.ImportSpecifier, SyntaxKind.ImportClause, SyntaxKind.NamespaceImport, SyntaxKind.ImportEqualsDeclaration));
                if (importDecl != null && !IsTypeOnlyImportDeclarationCore(importDecl))
                {
                    Error(importDecl, Diag.Import_0_conflicts_with_global_value_used_in_this_file_so_must_be_declared_with_a_type_only_import_when_isolatedModules_is_enabled, name);
                }
            }
        }
    }

    // checker.go:11635-11642
    internal bool CheckAndReportErrorForExtendingInterface(Node errorLocation)
    {
        var expression = GetEntityNameForExtendingInterface(errorLocation);
        if (expression != null && ResolveEntityName(expression, SymbolFlags.Interface, true /*ignoreErrors*/, false, null) != null)
        {
            Error(errorLocation, Diag.Cannot_extend_an_interface_0_Did_you_mean_implements, global::Ts.Scanner.Scanner.GetTextOfNode(expression));
            return true;
        }
        return false;
    }

    // checker.go:11648-11660
    private Node? GetEntityNameForExtendingInterface(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.PropertyAccessExpression:
                if (node.Parent != null)
                {
                    return GetEntityNameForExtendingInterface(node.Parent);
                }
                break;
            case SyntaxKind.ExpressionWithTypeArguments:
                if (AstUtilities.IsEntityNameExpression(node.Expression()!))
                {
                    return node.Expression();
                }
                break;
        }
        return null;
    }

    // utilities.go:139-144
    private static bool IsTypeReferenceIdentifier(Node node)
    {
        while (node.Parent!.Kind == SyntaxKind.QualifiedName)
        {
            node = node.Parent;
        }
        return NodeTests.IsTypeReferenceNode(node.Parent);
    }

    // utilities.go:146-159
    internal static bool IsInTypeQuery(Node node)
    {
        // TypeScript 1.0 spec (April 2014): 3.6.3
        // A type query consists of the keyword typeof followed by an expression.
        // The expression is restricted to a single identifier or a sequence of identifiers separated by periods
        return AstUtilities.FindAncestorOrQuit(node, n =>
        {
            switch (n.Kind)
            {
                case SyntaxKind.TypeQuery:
                    return FindAncestorResult.True;
                case SyntaxKind.Identifier:
                case SyntaxKind.QualifiedName:
                    return FindAncestorResult.False;
            }
            return FindAncestorResult.Quit;
        }) != null;
    }
}
