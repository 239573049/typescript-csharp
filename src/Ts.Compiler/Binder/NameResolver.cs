// Ported from tsgo internal/binder/nameresolver.go.
using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.Diagnostics;
using Diag = Ts.Diagnostics.Diagnostics;

namespace Ts.Binder;

// Go: nameresolver.go:12 — `Error func(...) *ast.Diagnostic` (variadic).
public delegate Diagnostic? NameResolverErrorHandler(Node? location, Message message, params object?[] args);

public sealed class NameResolver
{
    public CompilerOptions CompilerOptions = null!;
    public Func<Node, Symbol?>? GetSymbolOfDeclaration;
    public NameResolverErrorHandler? Error;
    public SymbolTable? Globals;
    public Symbol? ArgumentsSymbol;
    public Symbol? RequireSymbol;
    public Func<SymbolTable?, string, SymbolFlags, Symbol?>? Lookup;
    public Action<Symbol, SymbolFlags>? SymbolReferenced;
    public Action<Node, Tristate>? SetRequiresScopeChangeCache;
    public Func<Node, Tristate>? GetRequiresScopeChangeCache;
    public Func<Node?, string, Node?, Symbol?, bool>? OnPropertyWithInvalidInitializer;
    public Action<Node?, string, SymbolFlags, Message?>? OnFailedToResolveSymbol;
    public Action<Node?, Symbol, SymbolFlags, Node?, Node?, bool>? OnSuccessfullyResolvedSymbol;

    public Symbol? Resolve(Node? location, string name, SymbolFlags meaning, Message? nameNotFoundMessage, bool isUse, bool excludeGlobals)
    {
        Symbol? result = null;
        Node? lastLocation = null;
        Node? lastSelfReferenceLocation = null;
        Node? propertyWithInvalidInitializer = null;
        Node? associatedDeclarationForContainingInitializerOrBindingName = null;
        var withinDeferredContext = false;
        Node? grandparent;
        var originalLocation = location; // needed for did-you-mean error reporting, which gathers candidates starting from the original location
        var nameIsConst = name == "const";
        while (location != null)
        {
            if (nameIsConst && AstUtilities.IsConstAssertion(location))
            {
                // `const` in an `as const` has no symbol, but issues no error because there is no *actual* lookup of the type
                // (it refers to the constant type of the expression instead)
                return null;
            }
            if (AstUtilities.IsModuleOrEnumDeclaration(location) && lastLocation != null && location.Name() == lastLocation)
            {
                // If lastLocation is the name of a namespace or enum, skip the parent since it will have is own locals that could
                // conflict.
                lastLocation = location;
                location = location.Parent;
            }
            var locals = location!.Locals();
            // Locals of a source file are not in scope (because they get merged into the global symbol table)
            if (locals != null && !AstUtilities.IsGlobalSourceFile(location!))
            {
                result = LookupCore(locals, name, meaning);
                if (result != null)
                {
                    var useResult = true;
                    if (AstUtilities.IsFunctionLike(location) && lastLocation != null && lastLocation != location!.Body())
                    {
                        // symbol lookup restrictions for function-like declarations
                        // - Type parameters of a function are in scope in the entire function declaration, including the parameter
                        //   list and return type. However, local types are only in scope in the function body.
                        // - parameters are only in the scope of function body
                        // This restriction does not apply to JSDoc comment types because they are parented
                        // at a higher level than type parameters would normally be
                        if ((meaning & result.Flags & SymbolFlags.Type) != 0 && lastLocation.Kind != SyntaxKind.JSDoc)
                        {
                            // type parameters are visible in parameter list, return type and type parameter list.
                            // Synthetic fake scopes are added for signatures so type parameters are accessible from them.
                            useResult = (result.Flags & SymbolFlags.TypeParameter) != 0 &&
                                ((lastLocation.Flags & NodeFlags.Synthesized) != 0 ||
                                    lastLocation == location!.Type() ||
                                    lastLocation.Kind == SyntaxKind.Parameter ||
                                    lastLocation.Kind == SyntaxKind.JSDocParameterTag ||
                                    lastLocation.Kind == SyntaxKind.JSDocReturnTag ||
                                    lastLocation.Kind == SyntaxKind.TypeParameter);
                        }
                        if ((meaning & result.Flags & SymbolFlags.Variable) != 0)
                        {
                            // expression inside parameter will lookup as normal variable scope when targeting es2015+
                            if (UseOuterVariableScopeInParameter(result, location!, lastLocation))
                            {
                                useResult = false;
                            }
                            else if ((result.Flags & SymbolFlags.FunctionScopedVariable) != 0)
                            {
                                // parameters are visible only inside function body, parameter list and return type
                                // technically for parameter list case here we might mix parameters and variables declared in function,
                                // however it is detected separately when checking initializers of parameters
                                // to make sure that they reference no variables declared after them.
                                useResult = lastLocation.Kind == SyntaxKind.Parameter ||
                                    (lastLocation.Flags & NodeFlags.Synthesized) != 0 ||
                                    lastLocation == location!.Type() && AstUtilities.FindAncestor(result.ValueDeclaration, NodeTests.IsParameterDeclaration) != null;
                            }
                        }
                    }
                    else if (location!.Kind == SyntaxKind.ConditionalType)
                    {
                        // A type parameter declared using 'infer T' in a conditional type is visible only in
                        // the true branch of the conditional type.
                        useResult = lastLocation == location!.AsConditionalTypeNode().TrueType;
                    }
                    if (useResult)
                    {
                        goto loopEnd;
                    }
                    result = null;
                }
            }
            withinDeferredContext = withinDeferredContext || GetIsDeferredContext(location!, lastLocation);
            switch (location!.Kind)
            {
                case SyntaxKind.SourceFile:
                    if (!AstUtilities.IsExternalOrCommonJSModule(location.AsSourceFile()))
                    {
                        break;
                    }
                    goto case SyntaxKind.ModuleDeclaration;
                case SyntaxKind.ModuleDeclaration:
                    {
                        var moduleSymbol = GetSymbolOfDeclarationCore(location);
                        if (moduleSymbol == null)
                        {
                            break;
                        }
                        var moduleExports = moduleSymbol.Exports;
                        if (NodeTests.IsSourceFile(location) || (NodeTests.IsModuleDeclaration(location) && (location.Flags & NodeFlags.Ambient) != 0 && !AstUtilities.IsGlobalScopeAugmentation(location)))
                        {
                            // It's an external module. First see if the module has an export default and if the local
                            // name of that export default matches.
                            result = moduleExports?.GetValueOrDefault(InternalSymbolName.Default);
                            if (result != null)
                            {
                                var localSymbol = GetLocalSymbolForExportDefault(result);
                                if (localSymbol != null && (result.Flags & meaning) != 0 && localSymbol.Name == name)
                                {
                                    goto loopEnd;
                                }
                                result = null;
                            }
                            // Because of module/namespace merging, a module's exports are in scope,
                            // yet we never want to treat an export specifier as putting a member in scope.
                            // Therefore, if the name we find is purely an export specifier, it is not actually considered in scope.
                            // Two things to note about this:
                            //     1. We have to check this without calling getSymbol. The problem with calling getSymbol
                            //        on an export specifier is that it might find the export specifier itself, and try to
                            //        resolve it as an alias. This will cause the checker to consider the export specifier
                            //        a circular alias reference when it might not be.
                            //     2. We check === SymbolFlags.Alias in order to check that the symbol is *purely*
                            //        an alias. If we used &, we'd be throwing out symbols that have non alias aspects,
                            //        which is not the desired behavior.
                            var moduleExport = moduleExports?.GetValueOrDefault(name);
                            if (moduleExport != null && moduleExport.Flags == SymbolFlags.Alias && (AstUtilities.GetDeclarationOfKind(moduleExport, SyntaxKind.ExportSpecifier) != null || AstUtilities.GetDeclarationOfKind(moduleExport, SyntaxKind.NamespaceExport) != null))
                            {
                                break;
                            }
                        }
                        if (name != InternalSymbolName.Default)
                        {
                            if ((result = LookupCore(moduleExports, name, meaning & SymbolFlags.ModuleMember)) != null)
                            {
                                if (NodeTests.IsSourceFile(location) && location.AsSourceFile().CommonJSModuleIndicator != null && (result.Flags & SymbolFlags.Type) == 0)
                                {
                                    result = null;
                                }
                                else
                                {
                                    goto loopEnd;
                                }
                            }
                        }
                        break;
                    }
                case SyntaxKind.EnumDeclaration:
                    {
                        var enumSymbol = GetSymbolOfDeclarationCore(location);
                        if (enumSymbol == null)
                        {
                            break;
                        }
                        result = LookupCore(enumSymbol.Exports, name, meaning & SymbolFlags.EnumMember);
                        if (result != null)
                        {
                            if (nameNotFoundMessage != null && CompilerOptions.GetIsolatedModules() && (location.Flags & NodeFlags.Ambient) == 0 && AstUtilities.GetSourceFileOfNode(location) != AstUtilities.GetSourceFileOfNode(result.ValueDeclaration))
                            {
                                var isolatedModulesLikeFlagName = CoreUtil.IfElse(CompilerOptions.VerbatimModuleSyntax == Tristate.True, "verbatimModuleSyntax", "isolatedModules");
                                ReportError(originalLocation, Diag.Cannot_access_0_from_another_file_without_qualification_when_1_is_enabled_Use_2_instead,
                                    name, isolatedModulesLikeFlagName, enumSymbol.Name + "." + name);
                            }
                            goto loopEnd;
                        }
                        break;
                    }
                case SyntaxKind.PropertyDeclaration:
                    if (!AstUtilities.IsStatic(location))
                    {
                        var ctor = AstUtilities.FindConstructorDeclaration(location.Parent!);
                        if (ctor != null && ctor.Locals() != null)
                        {
                            if (LookupCore(ctor.Locals(), name, meaning & SymbolFlags.Value) != null)
                            {
                                // Remember the property node, it will be used later to report appropriate error
                                propertyWithInvalidInitializer = location;
                            }
                        }
                    }
                    break;
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.ClassExpression:
                case SyntaxKind.InterfaceDeclaration:
                    result = LookupCore(GetSymbolOfDeclarationCore(location)!.Members, name, meaning & SymbolFlags.Type);
                    if (result != null)
                    {
                        if (!IsTypeParameterSymbolDeclaredInContainer(result, location))
                        {
                            // ignore type parameters not declared in this container
                            result = null;
                            break;
                        }
                        if (lastLocation != null && AstUtilities.IsStatic(lastLocation))
                        {
                            // TypeScript 1.0 spec (April 2014): 3.4.1
                            // The scope of a type parameter extends over the entire declaration with which the type
                            // parameter list is associated, with the exception of static member declarations in classes.
                            if (nameNotFoundMessage != null)
                            {
                                ReportError(originalLocation, Diag.Static_members_cannot_reference_class_type_parameters);
                            }
                            return null;
                        }
                        goto loopEnd;
                    }
                    if (NodeTests.IsClassExpression(location) && (meaning & SymbolFlags.Class) != 0)
                    {
                        var className = location.Name();
                        if (className != null && name == className.Text())
                        {
                            result = location.Symbol();
                            goto loopEnd;
                        }
                    }
                    break;
                case SyntaxKind.ExpressionWithTypeArguments:
                    if (lastLocation == location.Expression() && NodeTests.IsHeritageClause(location.Parent!) && location.Parent!.AsHeritageClause().Token == SyntaxKind.ExtendsKeyword)
                    {
                        var container = location.Parent.Parent!;
                        if (AstUtilities.IsClassLike(container))
                        {
                            result = LookupCore(GetSymbolOfDeclarationCore(container)!.Members, name, meaning & SymbolFlags.Type);
                            if (result != null)
                            {
                                if (nameNotFoundMessage != null)
                                {
                                    ReportError(originalLocation, Diag.Base_class_expressions_cannot_reference_class_type_parameters);
                                }
                                return null;
                            }
                        }
                    }
                    break;
                // It is not legal to reference a class's own type parameters from a computed property name that
                // belongs to the class. For example:
                //
                //   function foo<T>() { return '' }
                //   class C<T> { // <-- Class's own type parameter T
                //       [foo<T>()]() { } // <-- Reference to T from class's own computed property
                //   }
                case SyntaxKind.ComputedPropertyName:
                    grandparent = location.Parent!.Parent;
                    if (AstUtilities.IsClassLike(grandparent!) || NodeTests.IsInterfaceDeclaration(grandparent!))
                    {
                        // A reference to this grandparent's type parameters would be an error
                        result = LookupCore(GetSymbolOfDeclarationCore(grandparent!)!.Members, name, meaning & SymbolFlags.Type);
                        if (result != null)
                        {
                            if (nameNotFoundMessage != null)
                            {
                                ReportError(originalLocation, Diag.A_computed_property_name_cannot_reference_a_type_parameter_from_its_containing_type);
                            }
                            return null;
                        }
                    }
                    break;
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.Constructor:
                case SyntaxKind.GetAccessor:
                case SyntaxKind.SetAccessor:
                case SyntaxKind.FunctionDeclaration:
                    if ((meaning & SymbolFlags.Variable) != 0 && name == "arguments")
                    {
                        result = ArgumentsSymbolCore();
                        goto loopEnd;
                    }
                    break;
                case SyntaxKind.FunctionExpression:
                    if ((meaning & SymbolFlags.Variable) != 0 && name == "arguments")
                    {
                        result = ArgumentsSymbolCore();
                        goto loopEnd;
                    }
                    if ((meaning & SymbolFlags.Function) != 0)
                    {
                        var functionName = location.AsFunctionExpression().Name();
                        if (functionName != null && name == functionName.Text())
                        {
                            result = location.Symbol();
                            goto loopEnd;
                        }
                    }
                    break;
                case SyntaxKind.Decorator:
                    // Decorators are resolved at the class declaration. Resolving at the parameter
                    // or member would result in looking up locals in the method.
                    //
                    //   function y() {}
                    //   class C {
                    //       method(@y x, y) {} // <-- decorator y should be resolved at the class declaration, not the parameter.
                    //   }
                    //
                    if (location.Parent != null && location.Parent.Kind == SyntaxKind.Parameter)
                    {
                        location = location.Parent;
                    }
                    //   function y() {}
                    //   class C {
                    //       @y method(x, y) {} // <-- decorator y should be resolved at the class declaration, not the method.
                    //   }
                    //
                    // class Decorators are resolved outside of the class to avoid referencing type parameters of that class.
                    //
                    //   type T = number;
                    //   declare function y(x: T): any;
                    //   @param(1 as T) // <-- T should resolve to the type alias outside of class C
                    //   class C<T> {}
                    if (location.Parent != null && (AstUtilities.IsClassElement(location.Parent) || location.Parent.Kind == SyntaxKind.ClassDeclaration))
                    {
                        location = location.Parent;
                    }
                    break;
                case SyntaxKind.Parameter:
                    var parameterDeclaration = location.AsParameterDeclaration();
                    if (lastLocation != null && (lastLocation == parameterDeclaration.Initializer ||
                        lastLocation == parameterDeclaration.Name() && AstUtilities.IsBindingPattern(lastLocation)))
                    {
                        if (associatedDeclarationForContainingInitializerOrBindingName == null)
                        {
                            associatedDeclarationForContainingInitializerOrBindingName = location;
                        }
                    }
                    break;
                case SyntaxKind.BindingElement:
                    var bindingElement = location.AsBindingElement();
                    if (lastLocation != null && (lastLocation == bindingElement.Initializer ||
                        lastLocation == bindingElement.Name() && AstUtilities.IsBindingPattern(lastLocation)))
                    {
                        if (AstUtilities.IsPartOfParameterDeclaration(location) && associatedDeclarationForContainingInitializerOrBindingName == null)
                        {
                            associatedDeclarationForContainingInitializerOrBindingName = location;
                        }
                    }
                    break;
                case SyntaxKind.InferType:
                    if ((meaning & SymbolFlags.TypeParameter) != 0)
                    {
                        var parameterName = location.AsInferTypeNode().TypeParameter!.AsTypeParameterDeclaration().Name();
                        if (parameterName != null && name == parameterName.Text())
                        {
                            result = location.AsInferTypeNode().TypeParameter!.Symbol();
                            goto loopEnd;
                        }
                    }
                    break;
                case SyntaxKind.ExportSpecifier:
                    var exportSpecifier = location.AsExportSpecifier();
                    if (lastLocation != null && lastLocation == exportSpecifier.PropertyName && location.Parent!.Parent!.ModuleSpecifier() != null)
                    {
                        location = location.Parent!.Parent!.Parent;
                    }
                    break;
            }
            if (IsSelfReferenceLocation(location!, lastLocation))
            {
                lastSelfReferenceLocation = location;
            }
            lastLocation = location;
            // !!! In Strada, JSDocTemplateTag/JSDocParameterTag/JSDocReturnTag locations skip to
            // getEffectiveContainerForJSDocTemplateTag/getHostSignatureFromJSDoc instead of location.parent.
            // This is a no-op currently because JSDoc nodes have no locals and getEffectiveJSDocHost is not
            // fully ported for JS assignment patterns.
            location = location!.Parent;
        }
    loopEnd:
        // We just climbed up parents looking for the name, meaning that we started in a descendant node of `lastLocation`.
        // If `result === lastSelfReferenceLocation.symbol`, that means that we are somewhere inside `lastSelfReferenceLocation` looking up a name, and resolving to `lastLocation` itself.
        // That means that this is a self-reference of `lastLocation`, and shouldn't count this when considering whether `lastLocation` is used.
        if (isUse && result != null && (lastSelfReferenceLocation == null || result != lastSelfReferenceLocation.Symbol()))
        {
            if (SymbolReferenced != null)
            {
                SymbolReferenced(result, meaning);
            }
        }
        if (result == null && !excludeGlobals)
        {
            result = LookupCore(Globals, name, meaning | SymbolFlags.GlobalLookup);
        }
        if (result == null)
        {
            if (originalLocation != null && AstUtilities.IsInJSFile(originalLocation) && originalLocation.Parent != null)
            {
                if (AstUtilities.IsRequireCall(originalLocation.Parent, false /*requireStringLiteralLikeArgument*/))
                {
                    return RequireSymbol;
                }
            }
        }
        if (nameNotFoundMessage != null)
        {
            if (propertyWithInvalidInitializer != null && OnPropertyWithInvalidInitializer != null && OnPropertyWithInvalidInitializer(originalLocation, name, propertyWithInvalidInitializer, result))
            {
                return null;
            }
            if (result == null)
            {
                if (OnFailedToResolveSymbol != null)
                {
                    OnFailedToResolveSymbol(originalLocation, name, meaning, nameNotFoundMessage);
                }
            }
            else
            {
                if (OnSuccessfullyResolvedSymbol != null)
                {
                    OnSuccessfullyResolvedSymbol(originalLocation, result, meaning, lastLocation, associatedDeclarationForContainingInitializerOrBindingName, withinDeferredContext);
                }
            }
        }
        return result;
    }

    private bool UseOuterVariableScopeInParameter(Symbol result, Node location, Node lastLocation)
    {
        if (NodeTests.IsParameterDeclaration(lastLocation))
        {
            var body = location.Body();
            if (body != null && result.ValueDeclaration != null && result.ValueDeclaration.Pos >= body.Pos && result.ValueDeclaration.End <= body.End)
            {
                // check for several cases where we introduce temporaries that require moving the name/initializer of the parameter to the body
                // - static field in a class expression
                // - optional chaining pre-es2020
                // - nullish coalesce pre-es2020
                // - spread assignment in binding pattern pre-es2017
                var functionLocation = location;
                var declarationRequiresScopeChange = Tristate.Unknown;
                if (GetRequiresScopeChangeCache != null)
                {
                    declarationRequiresScopeChange = GetRequiresScopeChangeCache(functionLocation);
                }
                if (declarationRequiresScopeChange == Tristate.Unknown)
                {
                    declarationRequiresScopeChange = CoreUtil.IfElse(CoreUtil.Some(functionLocation.Parameters(), RequiresScopeChange), Tristate.True, Tristate.False);
                    if (SetRequiresScopeChangeCache != null)
                    {
                        SetRequiresScopeChangeCache(functionLocation, declarationRequiresScopeChange);
                    }
                }
                return declarationRequiresScopeChange != Tristate.True;
            }
        }
        return false;
    }

    private bool RequiresScopeChange(Node node)
    {
        var d = node.AsParameterDeclaration();
        return RequiresScopeChangeWorker(d.Name()!) || d.Initializer != null && RequiresScopeChangeWorker(d.Initializer);
    }

    private bool RequiresScopeChangeWorker(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.Constructor:
                return false;
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.PropertyAssignment:
                return RequiresScopeChangeWorker(node.Name()!);
            case SyntaxKind.PropertyDeclaration:
                if (AstUtilities.HasStaticModifier(node))
                {
                    return !CompilerOptions.GetEmitStandardClassFields();
                }
                return RequiresScopeChangeWorker(node.AsPropertyDeclaration().Name()!);
            default:
                if (AstUtilities.IsNullishCoalesce(node) || AstUtilities.IsOptionalChain(node))
                {
                    return CompilerOptions.GetEmitScriptTarget() < ScriptTarget.ES2020;
                }
                if (NodeTests.IsBindingElement(node) && node.AsBindingElement().DotDotDotToken != null && NodeTests.IsObjectBindingPattern(node.Parent!))
                {
                    return CompilerOptions.GetEmitScriptTarget() < ScriptTarget.ES2017;
                }
                if (AstUtilities.IsTypeNode(node))
                {
                    return false;
                }
                return node.ForEachChild(RequiresScopeChangeWorker);
        }
    }

    // Go: nameresolver.go:402 `error`. Renamed to avoid clash with the `Error` hook field (§1.1).
    private void ReportError(Node? location, Message message, params object?[] args)
    {
        if (Error != null)
        {
            Error(location, message, args);
        }
        // Default implementation does not report errors
    }

    // Go: nameresolver.go:409 `getSymbolOfDeclaration`. Renamed to avoid clash with the hook field.
    private Symbol? GetSymbolOfDeclarationCore(Node node)
    {
        if (GetSymbolOfDeclaration != null)
        {
            return GetSymbolOfDeclaration(node);
        }

        // Default implementation does not support merged symbols
        return node.Symbol();
    }

    // Go: nameresolver.go:418 `lookup`. Renamed to avoid clash with the `Lookup` hook field.
    private Symbol? LookupCore(SymbolTable? symbols, string name, SymbolFlags meaning)
    {
        if (Lookup != null)
        {
            return Lookup(symbols, name, meaning);
        }
        // Default implementation does not support following aliases or merged symbols
        if (meaning != 0)
        {
            var symbol = symbols?.GetValueOrDefault(name);
            if (symbol != null)
            {
                if ((symbol.Flags & meaning) != 0)
                {
                    return symbol;
                }
            }
        }
        return null;
    }

    // Go: nameresolver.go:434 `argumentsSymbol`. Renamed to avoid clash with the field.
    private Symbol ArgumentsSymbolCore()
    {
        if (ArgumentsSymbol == null)
        {
            // Default implementation synthesizes a transient symbol for `arguments`
            ArgumentsSymbol = new Symbol { Name = "arguments", Flags = SymbolFlags.Property | SymbolFlags.Transient };
        }
        return ArgumentsSymbol;
    }

    public static Symbol? GetLocalSymbolForExportDefault(Symbol symbol)
    {
        if (!IsExportDefaultSymbol(symbol) || (symbol.Declarations?.Length ?? 0) == 0)
        {
            return null;
        }
        foreach (var decl in symbol.Declarations!)
        {
            var localSymbol = decl.LocalSymbol();
            if (localSymbol != null)
            {
                return localSymbol;
            }
        }
        return null;
    }

    private static bool IsExportDefaultSymbol(Symbol? symbol)
    {
        return symbol != null && (symbol.Declarations?.Length ?? 0) > 0 && AstUtilities.HasSyntacticModifier(symbol.Declarations![0], ModifierFlags.Default);
    }

    private static bool GetIsDeferredContext(Node location, Node? lastLocation)
    {
        if (location.Kind != SyntaxKind.ArrowFunction && location.Kind != SyntaxKind.FunctionExpression)
        {
            // initializers in instance property declaration of class like entities are executed in constructor and thus deferred
            // A name is evaluated within the enclosing scope - so it shouldn't count as deferred
            return NodeTests.IsTypeQueryNode(location) ||
                (AstUtilities.IsFunctionLikeDeclaration(location) || location.Kind == SyntaxKind.PropertyDeclaration && !AstUtilities.IsStatic(location)) &&
                    (lastLocation == null || lastLocation != location.Name());
        }
        if (lastLocation != null && lastLocation == location.Name())
        {
            return false;
        }
        // generator functions and async functions are not inlined in control flow when immediately invoked
        if (location.BodyData()!.AsteriskToken != null || AstUtilities.HasSyntacticModifier(location, ModifierFlags.Async))
        {
            return true;
        }
        return AstUtilities.GetImmediatelyInvokedFunctionExpression(location) == null;
    }

    private static bool IsTypeParameterSymbolDeclaredInContainer(Symbol symbol, Node container)
    {
        foreach (var decl in symbol.Declarations ?? [])
        {
            if (decl.Kind == SyntaxKind.TypeParameter)
            {
                var parent = decl.Parent;
                if (parent == container)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsSelfReferenceLocation(Node node, Node? lastLocation)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Parameter:
                return lastLocation != null && lastLocation == node.Name();
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
            case SyntaxKind.ModuleDeclaration: // For `namespace N { N; }`
                return true;
        }
        return false;
    }
}
