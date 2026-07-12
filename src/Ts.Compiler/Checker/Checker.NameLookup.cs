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

    // checker.go:1874
    internal void CheckResolvedBlockScopedVariable(Symbol result, Node errorLocation)
    {
        global::Ts.Core.Debug.Assert((result.Flags & SymbolFlags.BlockScopedVariable) != 0 || (result.Flags & SymbolFlags.Class) != 0 || (result.Flags & SymbolFlags.Enum) != 0);
        if ((result.Flags & (SymbolFlags.Function | SymbolFlags.FunctionScopedVariable | SymbolFlags.Assignment)) != 0 && (result.Flags & SymbolFlags.Class) != 0)
        {
            // constructor functions aren't block scoped
            return;
        }
        // Block-scoped variables cannot be used before their definition
        var declaration = CoreUtil.Find(result.Declarations, d =>
            AstUtilities.IsBlockOrCatchScoped(d) || AstUtilities.IsClassLike(d) || NodeTests.IsEnumDeclaration(d));
        if (declaration == null)
        {
            throw new global::System.InvalidOperationException("checkResolvedBlockScopedVariable could not find block-scoped declaration");
        }
        if ((declaration.Flags & NodeFlags.Ambient) == 0 && !IsBlockScopedNameDeclaredBeforeUse(declaration, errorLocation))
        {
            Diagnostic? diagnostic = null;
            var declarationName = global::Ts.Scanner.Scanner.DeclarationNameToString(AstUtilities.GetNameOfDeclaration(declaration));
            if ((result.Flags & SymbolFlags.BlockScopedVariable) != 0)
            {
                diagnostic = Error(errorLocation, Diag.Block_scoped_variable_0_used_before_its_declaration, declarationName);
            }
            else if ((result.Flags & SymbolFlags.Class) != 0)
            {
                diagnostic = Error(errorLocation, Diag.Class_0_used_before_its_declaration, declarationName);
            }
            else if ((result.Flags & SymbolFlags.RegularEnum) != 0)
            {
                diagnostic = Error(errorLocation, Diag.Enum_0_used_before_its_declaration, declarationName);
            }
            else
            {
                global::Ts.Core.Debug.Assert((result.Flags & SymbolFlags.ConstEnum) != 0);
                if (compilerOptions.GetIsolatedModules())
                {
                    diagnostic = Error(errorLocation, Diag.Enum_0_used_before_its_declaration, declarationName);
                }
            }
            if (diagnostic != null)
            {
                diagnostic.AddRelatedInfo(CreateDiagnosticForNode(declaration, Diag.X_0_is_declared_here, declarationName));
            }
        }
    }

    // checker.go:1908
    internal bool IsBlockScopedNameDeclaredBeforeUse(Node declaration, Node usage)
    {
        var declarationFile = AstUtilities.GetSourceFileOfNode(declaration);
        var useFile = AstUtilities.GetSourceFileOfNode(usage);
        var declContainer = AstUtilities.GetEnclosingBlockScopeContainer(declaration);
        if (declarationFile != useFile)
        {
            // nodes are in different files and order cannot be determined
            return true;
        }
        // deferred usage in a type context is always OK regardless of the usage position:
        if ((usage.Flags & NodeFlags.JSDoc) != 0 || IsInTypeQuery(usage) || IsInAmbientOrTypeNode(usage))
        {
            return true;
        }
        if (declaration.Pos <= usage.Pos && !(NodeTests.IsPropertyDeclaration(declaration) && IsThisProperty(usage.Parent!) && declaration.Initializer() == null && !IsExclamationToken(declaration.PostfixToken())))
        {
            // declaration is before usage
            switch (declaration.Kind)
            {
                case SyntaxKind.BindingElement:
                    // still might be illegal if declaration and usage are both binding elements (eg var [a = b, b = b] = [1, 2])
                    var errorBindingElement = AstUtilities.FindAncestorKind(usage, SyntaxKind.BindingElement);
                    if (errorBindingElement != null)
                    {
                        return AstUtilities.FindAncestor(errorBindingElement, NodeTests.IsBindingElement) != AstUtilities.FindAncestor(declaration, NodeTests.IsBindingElement) || declaration.Pos < errorBindingElement.Pos;
                    }
                    // or it might be illegal if usage happens before parent variable is declared (eg var [a] = a)
                    return IsBlockScopedNameDeclaredBeforeUse(AstUtilities.FindAncestorKind(declaration, SyntaxKind.VariableDeclaration)!, usage);
                case SyntaxKind.VariableDeclaration:
                    // still might be illegal if usage is in the initializer of the variable declaration (eg var a = a)
                    return !IsImmediatelyUsedInInitializerOfBlockScopedVariable(declaration, usage, declContainer);
            }
            if (AstUtilities.IsClassLike(declaration))
            {
                // still might be illegal if the usage is within a computed property name in the class (eg class A { static p = "a"; [A.p]() {} })
                // or when used within a decorator in the class (e.g. `@dec(A.x) class A { static x = "x" }`),
                // except when used in a function that is not an IIFE (e.g., `@dec(() => A.x) class A { ... }`)
                var container = usage;
                while (container != null && container != declaration)
                {
                    if ((NodeTests.IsComputedPropertyName(container) && container.Parent!.Parent == declaration) ||
                        (!legacyDecorators && NodeTests.IsDecorator(container) && (container.Parent == declaration ||
                            (NodeTests.IsMethodDeclaration(container.Parent!) && container.Parent!.Parent == declaration) ||
                            (AstUtilities.IsAccessor(container.Parent!) && container.Parent!.Parent == declaration) ||
                            (NodeTests.IsPropertyDeclaration(container.Parent!) && container.Parent!.Parent == declaration) ||
                            (NodeTests.IsParameterDeclaration(container.Parent!) && container.Parent!.Parent!.Parent == declaration))))
                    {
                        break;
                    }
                    container = container.Parent;
                }
                if (container == null || container == declaration)
                {
                    return true;
                }
                if (!legacyDecorators && NodeTests.IsDecorator(container))
                {
                    var n = usage;
                    while (n != null && n != container)
                    {
                        if (AstUtilities.IsFunctionLike(n) && AstUtilities.GetImmediatelyInvokedFunctionExpression(n) == null)
                        {
                            break;
                        }
                        n = n.Parent;
                    }
                    return n != null && n != container;
                }
                return false;
            }
            if (NodeTests.IsPropertyDeclaration(declaration))
            {
                // still might be illegal if a self-referencing property initializer (eg private x = this.x)
                return !IsPropertyImmediatelyReferencedWithinDeclaration(declaration, usage, false /*stopAtAnyPropertyDeclaration*/);
            }
            if (AstUtilities.IsParameterPropertyDeclaration(declaration, declaration.Parent!))
            {
                // foo = this.bar is illegal in emitStandardClassFields when bar is a parameter property
                return !(emitStandardClassFields && AstUtilities.GetContainingClass(declaration) == AstUtilities.GetContainingClass(usage) && IsUsedInFunctionOrInstanceProperty(usage, declaration, declContainer));
            }
            return true;
        }
        // declaration is after usage, but it can still be legal if usage is deferred:
        // 1. inside an export specifier
        // 2. inside a function
        // 3. inside an instance property initializer, a reference to a non-instance property
        //    (except when emitStandardClassFields: true and the reference is to a parameter property)
        // 4. inside a static property initializer, a reference to a static method in the same class
        // 5. inside a TS export= declaration (since we will move the export statement during emit to avoid TDZ)
        if (NodeTests.IsExportSpecifier(usage.Parent!) || (NodeTests.IsExportAssignment(usage.Parent!) && usage.Parent!.AsExportAssignment().IsExportEquals))
        {
            // export specifiers do not use the variable, they only make it available for use
            return true;
        }
        // When resolving symbols for exports, the `usage` location passed in can be the export site directly
        if (NodeTests.IsExportAssignment(usage) && usage.AsExportAssignment().IsExportEquals)
        {
            return true;
        }
        if (IsUsedInFunctionOrInstanceProperty(usage, declaration, declContainer))
        {
            if (emitStandardClassFields && AstUtilities.GetContainingClass(declaration) != null && (NodeTests.IsPropertyDeclaration(declaration) || AstUtilities.IsParameterPropertyDeclaration(declaration, declaration.Parent!)))
            {
                return !IsPropertyImmediatelyReferencedWithinDeclaration(declaration, usage, true /*stopAtAnyPropertyDeclaration*/);
            }
            return true;
        }
        return false;
    }

    // checker.go:1997
    internal bool IsUsedInFunctionOrInstanceProperty(Node usage, Node declaration, Node? declContainer)
    {
        return AstUtilities.FindAncestorOrQuit(usage, current =>
        {
            if (current == declContainer)
            {
                return FindAncestorResult.Quit;
            }
            if (AstUtilities.IsFunctionLike(current))
            {
                return AstUtilities.ToFindAncestorResult(AstUtilities.GetImmediatelyInvokedFunctionExpression(current) == null);
            }
            if (NodeTests.IsClassStaticBlockDeclaration(current))
            {
                return AstUtilities.ToFindAncestorResult(declaration.Pos < usage.Pos);
            }

            if (current.Parent != null && NodeTests.IsPropertyDeclaration(current.Parent))
            {
                var propertyDeclaration = current.Parent;
                var initializerOfProperty = propertyDeclaration.Initializer() == current;
                if (initializerOfProperty)
                {
                    if (AstUtilities.IsStatic(current.Parent))
                    {
                        if (NodeTests.IsMethodDeclaration(declaration))
                        {
                            return FindAncestorResult.True;
                        }
                        if (NodeTests.IsPropertyDeclaration(declaration) && AstUtilities.GetContainingClass(usage) == AstUtilities.GetContainingClass(declaration))
                        {
                            var propName = declaration.Name()!;
                            if (NodeTests.IsIdentifier(propName) || NodeTests.IsPrivateIdentifier(propName))
                            {
                                var t = GetTypeOfSymbol(GetSymbolOfDeclaration(declaration)!);
                                var staticBlocks = CoreUtil.Filter(declaration.Parent!.Members(), NodeTests.IsClassStaticBlockDeclaration);
                                if (IsPropertyInitializedInStaticBlocks(propName, t, staticBlocks.OrEmpty(), declaration.Parent!.Pos, current.Pos))
                                {
                                    return FindAncestorResult.True;
                                }
                            }
                        }
                    }
                    else
                    {
                        var isDeclarationInstanceProperty = NodeTests.IsPropertyDeclaration(declaration) && !AstUtilities.IsStatic(declaration);
                        if (!isDeclarationInstanceProperty || AstUtilities.GetContainingClass(usage) != AstUtilities.GetContainingClass(declaration))
                        {
                            return FindAncestorResult.True;
                        }
                    }
                }
            }

            if (current.Parent != null && NodeTests.IsDecorator(current.Parent))
            {
                var decorator = current.Parent.AsDecorator();
                if (decorator.Expression == current)
                {
                    if (NodeTests.IsParameterDeclaration(current.Parent.Parent!))
                    {
                        if (IsUsedInFunctionOrInstanceProperty(current.Parent.Parent!.Parent!.Parent!, declaration, declContainer))
                        {
                            return FindAncestorResult.True;
                        }
                        return FindAncestorResult.Quit;
                    }
                    if (NodeTests.IsMethodDeclaration(current.Parent.Parent!))
                    {
                        if (IsUsedInFunctionOrInstanceProperty(current.Parent.Parent!.Parent!, declaration, declContainer))
                        {
                            return FindAncestorResult.True;
                        }
                        return FindAncestorResult.Quit;
                    }
                }
            }

            return FindAncestorResult.False;
        }) != null;
    }

    // checker.go:2058
    internal static bool IsImmediatelyUsedInInitializerOfBlockScopedVariable(Node declaration, Node usage, Node? declContainer)
    {
        switch (declaration.Parent!.Parent!.Kind)
        {
            case SyntaxKind.VariableStatement:
            case SyntaxKind.ForStatement:
            case SyntaxKind.ForOfStatement:
                // variable statement/for/for-of statement case,
                // use site should not be inside variable declaration (initializer of declaration or binding element)
                if (IsSameScopeDescendentOf(usage, declaration, declContainer))
                {
                    return true;
                }
                break;
        }
        // ForIn/ForOf case - use site should not be used in expression part
        var grandparent = declaration.Parent!.Parent!;
        return AstUtilities.IsForInOrOfStatement(grandparent) && IsSameScopeDescendentOf(usage, grandparent.Expression(), declContainer);
    }

    // Starting from 'initial' node walk up the parent chain until 'stopAt' node is reached.
    // If at any point current node is equal to 'parent' node - return true.
    // If current node is an IIFE, continue walking up.
    // Return false if 'stopAt' node is reached or isFunctionLike(current) === true.
    // checker.go:2076
    internal static bool IsSameScopeDescendentOf(Node initial, Node? parent, Node? stopAt)
    {
        if (parent == null)
        {
            return false;
        }
        for (var n = initial; n != null; n = n.Parent)
        {
            if (n == parent)
            {
                return true;
            }
            if (n == stopAt || (AstUtilities.IsFunctionLike(n) && (AstUtilities.GetImmediatelyInvokedFunctionExpression(n) == null || (AstUtilities.GetFunctionFlags(n) & FunctionFlags.AsyncGenerator) != 0)))
            {
                return false;
            }
        }
        return false;
    }

    // isPropertyImmediatelyReferencedWithinDeclaration is used for detecting ES-standard class field use-before-def errors
    // checker.go:2092
    internal static bool IsPropertyImmediatelyReferencedWithinDeclaration(Node declaration, Node usage, bool stopAtAnyPropertyDeclaration)
    {
        // always legal if usage is after declaration
        if (usage.End > declaration.End)
        {
            return false;
        }
        // still might be legal if usage is deferred (e.g. x: any = () => this.x)
        // otherwise illegal if immediately referenced within the declaration (e.g. x: any = this.x)
        for (var node = usage; node != null && node != declaration; node = node.Parent)
        {
            switch (node.Kind)
            {
                case SyntaxKind.ArrowFunction:
                    return false;
                case SyntaxKind.PropertyDeclaration:
                    // even when stopping at any property declaration, they need to come from the same class
                    return stopAtAnyPropertyDeclaration &&
                        ((NodeTests.IsPropertyDeclaration(declaration) && node.Parent == declaration.Parent) ||
                            (AstUtilities.IsParameterPropertyDeclaration(declaration, declaration.Parent!) && node.Parent == declaration.Parent!.Parent));
                case SyntaxKind.Block:
                    switch (node.Parent!.Kind)
                    {
                        case SyntaxKind.MethodDeclaration:
                        case SyntaxKind.GetAccessor:
                        case SyntaxKind.SetAccessor:
                            return false;
                    }
                    break;
            }
        }
        return true;
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
