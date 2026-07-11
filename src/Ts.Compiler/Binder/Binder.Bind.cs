// Ported from tsgo internal/binder/binder.go (declaration binding).
using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.Diagnostics;
using Diag = Ts.Diagnostics.Diagnostics;

namespace Ts.Binder;

public sealed partial class Binder
{
    private bool Bind(Node? node)
    {
        if (node == null)
        {
            return false;
        }
        // Even though in the AST the jsdoc @typedef node belongs to the current node,
        // its symbol might be in the same scope with the current node's symbol. Consider:
        //
        //     /** @typedef {string | number} MyType */
        //     function foo();
        //
        // Here the current node is "foo", which is a container, but the scope of "MyType" should
        // not be inside "foo". Therefore we always bind @typedef before bind the parent node,
        // and skip binding this tag later when binding all the other jsdoc tags.

        // First we bind declaration nodes to a symbol if possible. We'll both create a symbol
        // and then potentially add the symbol to an appropriate symbol table. Possible
        // destination symbol tables are:
        //
        //  1) The 'exports' table of the current container's symbol.
        //  2) The 'members' table of the current container's symbol.
        //  3) The 'locals' table of the current container.
        //
        // However, not all symbols will end up in any of these tables. 'Anonymous' symbols
        // (like TypeLiterals for example) will not be put in any table.
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                node.AsIdentifier().FlowNode = currentFlow;
                CheckContextualIdentifier(node);
                break;
            case SyntaxKind.ThisKeyword:
            case SyntaxKind.SuperKeyword:
                if (node.Kind == SyntaxKind.ThisKeyword)
                {
                    seenThisKeyword = true;
                }
                node.AsKeywordExpression().FlowNode = currentFlow;
                break;
            case SyntaxKind.QualifiedName:
                if (currentFlow != null && AstUtilities.IsPartOfTypeQuery(node))
                {
                    node.AsQualifiedName().FlowNode = currentFlow;
                }
                break;
            case SyntaxKind.MetaProperty:
                node.AsMetaProperty().FlowNode = currentFlow;
                break;
            case SyntaxKind.PrivateIdentifier:
                CheckPrivateIdentifier(node);
                break;
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ElementAccessExpression:
                if (currentFlow != null && IsNarrowableReference(node))
                {
                    SetFlowNode(node, currentFlow);
                }
                break;
            case SyntaxKind.BinaryExpression:
                switch (AstUtilities.GetAssignmentDeclarationKind(node))
                {
                    case JSDeclarationKind.ModuleExports:
                        BindModuleExportsAssignment(node);
                        break;
                    case JSDeclarationKind.ExportsProperty:
                        BindExportsOrObjectDefineProperty(node);
                        break;
                    case JSDeclarationKind.Property:
                        BindExpandoPropertyAssignment(node);
                        break;
                    case JSDeclarationKind.ThisProperty:
                        BindThisPropertyAssignment(node);
                        break;
                }
                CheckStrictModeBinaryExpression(node);
                break;
            case SyntaxKind.CatchClause:
                CheckStrictModeCatchClause(node);
                break;
            case SyntaxKind.DeleteExpression:
                CheckStrictModeDeleteExpression(node);
                break;
            case SyntaxKind.PostfixUnaryExpression:
                CheckStrictModePostfixUnaryExpression(node);
                break;
            case SyntaxKind.PrefixUnaryExpression:
                CheckStrictModePrefixUnaryExpression(node);
                break;
            case SyntaxKind.WithStatement:
                CheckStrictModeWithStatement(node);
                break;
            case SyntaxKind.LabeledStatement:
                CheckStrictModeLabeledStatement(node);
                break;
            case SyntaxKind.ThisType:
                seenThisKeyword = true;
                break;
            case SyntaxKind.TypeParameter:
                BindTypeParameter(node);
                break;
            case SyntaxKind.Parameter:
                BindParameter(node);
                break;
            case SyntaxKind.VariableDeclaration:
                BindVariableDeclarationOrBindingElement(node);
                break;
            case SyntaxKind.BindingElement:
                node.AsBindingElement().FlowNode = currentFlow;
                BindVariableDeclarationOrBindingElement(node);
                break;
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
                BindPropertyWorker(node);
                break;
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.ShorthandPropertyAssignment:
                BindPropertyOrMethodOrAccessor(node, SymbolFlags.Property, SymbolFlags.PropertyExcludes);
                break;
            case SyntaxKind.EnumMember:
                BindPropertyOrMethodOrAccessor(node, SymbolFlags.EnumMember, SymbolFlags.EnumMemberExcludes);
                break;
            case SyntaxKind.CallSignature:
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.IndexSignature:
                DeclareSymbolAndAddToSymbolTable(node, SymbolFlags.Signature, SymbolFlags.None);
                break;
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
                BindPropertyOrMethodOrAccessor(node, SymbolFlags.Method | GetOptionalSymbolFlagForNode(node), CoreUtil.IfElse(AstUtilities.IsObjectLiteralMethod(node), SymbolFlags.Value, SymbolFlags.MethodExcludes));
                break;
            case SyntaxKind.FunctionDeclaration:
                BindFunctionDeclaration(node);
                break;
            case SyntaxKind.Constructor:
                DeclareSymbolAndAddToSymbolTable(node, SymbolFlags.Constructor, SymbolFlags.None);
                break;
            case SyntaxKind.GetAccessor:
                BindPropertyOrMethodOrAccessor(node, SymbolFlags.GetAccessor, SymbolFlags.GetAccessorExcludes);
                break;
            case SyntaxKind.SetAccessor:
                BindPropertyOrMethodOrAccessor(node, SymbolFlags.SetAccessor, SymbolFlags.SetAccessorExcludes);
                break;
            case SyntaxKind.FunctionType:
            case SyntaxKind.ConstructorType:
                BindFunctionOrConstructorType(node);
                break;
            case SyntaxKind.TypeLiteral:
            case SyntaxKind.MappedType:
                BindAnonymousDeclaration(node, SymbolFlags.TypeLiteral, InternalSymbolName.Type);
                break;
            case SyntaxKind.ObjectLiteralExpression:
                BindAnonymousDeclaration(node, SymbolFlags.ObjectLiteral, InternalSymbolName.Object);
                break;
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
                BindFunctionExpression(node);
                break;
            case SyntaxKind.ClassExpression:
            case SyntaxKind.ClassDeclaration:
                BindClassLikeDeclaration(node);
                break;
            case SyntaxKind.InterfaceDeclaration:
                BindBlockScopedDeclaration(node, SymbolFlags.Interface, SymbolFlags.InterfaceExcludes);
                break;
            case SyntaxKind.CallExpression:
                switch (AstUtilities.GetAssignmentDeclarationKind(node))
                {
                    case JSDeclarationKind.ObjectDefinePropertyValue:
                        BindExpandoPropertyAssignment(node);
                        break;
                    case JSDeclarationKind.ObjectDefinePropertyExports:
                        BindExportsOrObjectDefineProperty(node);
                        break;
                }
                if (AstUtilities.IsInJSFile(node))
                {
                    BindCallExpression(node);
                }
                break;
            case SyntaxKind.TypeAliasDeclaration:
                BindBlockScopedDeclaration(node, SymbolFlags.TypeAlias, SymbolFlags.TypeAliasExcludes);
                break;
            case SyntaxKind.JSTypeAliasDeclaration:
                // Top-level JSTypeAliasDeclaration nodes are processed in bindContainer
                if (!NodeTests.IsSourceFile(blockScopeContainer!))
                {
                    BindBlockScopedDeclaration(node, SymbolFlags.TypeAlias, SymbolFlags.TypeAliasExcludes);
                }
                break;
            case SyntaxKind.EnumDeclaration:
                BindEnumDeclaration(node);
                break;
            case SyntaxKind.ModuleDeclaration:
                BindModuleDeclaration(node);
                break;
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.NamespaceImport:
            case SyntaxKind.ImportSpecifier:
            case SyntaxKind.ExportSpecifier:
                DeclareSymbolAndAddToSymbolTable(node, SymbolFlags.Alias, SymbolFlags.AliasExcludes);
                break;
            case SyntaxKind.NamespaceExportDeclaration:
                BindNamespaceExportDeclaration(node);
                break;
            case SyntaxKind.ImportClause:
                BindImportClause(node);
                break;
            case SyntaxKind.ExportDeclaration:
                BindExportDeclaration(node);
                break;
            case SyntaxKind.ExportAssignment:
                BindExportAssignment(node);
                break;
            case SyntaxKind.SourceFile:
                BindSourceFileIfExternalModule();
                break;
            case SyntaxKind.JsxAttributes:
                BindJsxAttributes(node);
                break;
            case SyntaxKind.JsxAttribute:
                BindJsxAttribute(node, SymbolFlags.Property, SymbolFlags.PropertyExcludes);
                break;
        }
        // Then we recurse into the children of the node to bind them as well. For certain
        // symbols we do specialized work when we recurse. For example, we'll keep track of
        // the current 'container' node when it changes. This helps us know which symbol table
        // a local should go into for example. Since terminal nodes are known not to have
        // children, as an optimization we don't process those.
        var thisNodeOrAnySubnodesHasError = (node.Flags & NodeFlags.ThisNodeHasError) != 0;
        if (node.Kind > SyntaxKind.LastToken)
        {
            var saveSeenParseError = seenParseError;
            seenParseError = false;
            var containerFlags = GetContainerFlags(node);
            if (containerFlags == ContainerFlags.None)
            {
                BindChildren(node);
            }
            else
            {
                BindContainer(node, containerFlags);
            }
            if (seenParseError)
            {
                thisNodeOrAnySubnodesHasError = true;
            }
            seenParseError = saveSeenParseError;
        }
        if (thisNodeOrAnySubnodesHasError)
        {
            node.Flags |= NodeFlags.ThisNodeOrAnySubNodesHasError;
            seenParseError = true;
        }
        return false;
    }

    private void BindPropertyWorker(Node node)
    {
        var isAutoAccessor = AstUtilities.IsAutoAccessorPropertyDeclaration(node);
        var includes = CoreUtil.IfElse(isAutoAccessor, SymbolFlags.Accessor, SymbolFlags.Property);
        var excludes = CoreUtil.IfElse(isAutoAccessor, SymbolFlags.AccessorExcludes, SymbolFlags.PropertyExcludes);
        BindPropertyOrMethodOrAccessor(node, includes | GetOptionalSymbolFlagForNode(node), excludes);
    }

    private void BindSourceFileIfExternalModule()
    {
        SetExportContextFlag(file.AsNode());
        if (AstUtilities.IsExternalOrCommonJSModule(file))
        {
            BindSourceFileAsExternalModule();
        }
        else if (AstUtilities.IsJsonSourceFile(file))
        {
            BindSourceFileAsExternalModule();
            // Create symbol equivalent for the module.exports = {}
            var originalSymbol = file.Symbol;
            DeclareSymbol(AstUtilities.GetExports(file.Symbol!), file.Symbol, file.AsNode(), SymbolFlags.Property, SymbolFlags.All);
            file.Symbol = originalSymbol;
        }
    }

    private void BindSourceFileAsExternalModule()
    {
        BindAnonymousDeclaration(file.AsNode(), SymbolFlags.ValueModule, "\"" + global::Ts.TsPath.TsPath.RemoveFileExtension(file.FileName()) + "\"");
    }

    private void BindModuleDeclaration(Node node)
    {
        SetExportContextFlag(node);
        if (AstUtilities.IsAmbientModule(node))
        {
            if (AstUtilities.HasSyntacticModifier(node, ModifierFlags.Export))
            {
                ErrorOnFirstToken(node, Diag.X_export_modifier_cannot_be_applied_to_ambient_modules_and_module_augmentations_since_they_are_always_visible);
            }
            if (AstUtilities.IsModuleAugmentationExternal(node))
            {
                DeclareModuleSymbol(node);
            }
            else
            {
                var name = node.AsModuleDeclaration().Name();
                var symbol = DeclareSymbolAndAddToSymbolTable(node, SymbolFlags.ValueModule, SymbolFlags.ValueModuleExcludes);

                if (NodeTests.IsStringLiteral(name!))
                {
                    var pattern = global::Ts.Core.Core.TryParsePattern(name!.Text());
                    if (!pattern.IsValid)
                    {
                        // An invalid pattern - must have multiple wildcards.
                        ErrorOnFirstToken(name!, Diag.Pattern_0_can_have_at_most_one_Asterisk_character, name!.Text());
                    }
                    else if (pattern.StarIndex >= 0)
                    {
                        file.PatternAmbientModules = [.. (file.PatternAmbientModules ?? []), new PatternAmbientModule { Pattern = pattern, Symbol = symbol }];
                    }
                }
            }
        }
        else
        {
            var state = DeclareModuleSymbol(node);
            if (state != ModuleInstanceState.NonInstantiated)
            {
                var symbol = node.Symbol()!;
                // if module was already merged with some function, class or non-const enum, treat it as non-const-enum-only
                var constEnumOnlyModule = ((symbol.Flags & (SymbolFlags.Function | SymbolFlags.Class | SymbolFlags.RegularEnum)) == 0) &&
                    // Current must be `const enum` only
                    state == ModuleInstanceState.ConstEnumOnly &&
                    // Can't have been set to 'false' in a previous merged symbol. ('undefined' OK)
                    !notConstEnumOnlyModules.Has(symbol);
                if (constEnumOnlyModule)
                {
                    symbol.Flags |= SymbolFlags.ConstEnumOnlyModule;
                }
                else
                {
                    symbol.Flags &= ~SymbolFlags.ConstEnumOnlyModule;
                    notConstEnumOnlyModules.Add(symbol);
                }
            }
        }
    }

    private ModuleInstanceState DeclareModuleSymbol(Node node)
    {
        var state = AstUtilities.GetModuleInstanceState(node);
        var instantiated = state != ModuleInstanceState.NonInstantiated;
        DeclareSymbolAndAddToSymbolTable(node, CoreUtil.IfElse(instantiated, SymbolFlags.ValueModule, SymbolFlags.NamespaceModule), CoreUtil.IfElse(instantiated, SymbolFlags.ValueModuleExcludes, SymbolFlags.NamespaceModuleExcludes));
        return state;
    }

    private void BindNamespaceExportDeclaration(Node node)
    {
        if (node.Modifiers() != null)
        {
            ErrorOnNode(node, Diag.Modifiers_cannot_appear_here);
        }
        if (!NodeTests.IsSourceFile(node.Parent!))
        {
            ErrorOnNode(node, Diag.Global_module_exports_may_only_appear_at_top_level);
        }
        else if (!AstUtilities.IsExternalModule(node.Parent!.AsSourceFile()))
        {
            ErrorOnNode(node, Diag.Global_module_exports_may_only_appear_in_module_files);
        }
        else if (!node.Parent!.AsSourceFile().IsDeclarationFile)
        {
            ErrorOnNode(node, Diag.Global_module_exports_may_only_appear_in_declaration_files);
        }
        else
        {
            DeclareSymbol(AstUtilities.GetSymbolTable(ref file.GlobalExports), file.Symbol, node, SymbolFlags.Alias, SymbolFlags.AliasExcludes);
        }
    }

    private void BindImportClause(Node node)
    {
        if (node.AsImportClause().Name() != null)
        {
            DeclareSymbolAndAddToSymbolTable(node, SymbolFlags.Alias, SymbolFlags.AliasExcludes);
        }
    }

    private void BindExportDeclaration(Node node)
    {
        var decl = node.AsExportDeclaration();
        if (container!.Symbol() == null)
        {
            // Export * in some sort of block construct
            BindAnonymousDeclaration(node, SymbolFlags.ExportStar, GetDeclarationName(node));
        }
        else if (decl.ExportClause == null)
        {
            // All export * declarations are collected in an __export symbol
            DeclareSymbol(AstUtilities.GetExports(container!.Symbol()!), container!.Symbol(), node, SymbolFlags.ExportStar, SymbolFlags.None);
        }
        else if (NodeTests.IsNamespaceExport(decl.ExportClause))
        {
            DeclareSymbol(AstUtilities.GetExports(container!.Symbol()!), container!.Symbol(), decl.ExportClause, SymbolFlags.Alias, SymbolFlags.AliasExcludes);
        }
    }

    private void BindExportAssignment(Node node)
    {
        var container = this.container!;
        if (container.Symbol() == null && NodeTests.IsExportAssignment(node))
        {
            // Incorrect export assignment in some sort of block construct
            BindAnonymousDeclaration(node, SymbolFlags.Value, GetDeclarationName(node));
        }
        else
        {
            // If there is an `export default x;` alias declaration, can't `export default` anything else.
            // (In contrast, you can still have `export default function f() {}` and `export default interface I {}`.)
            var flags = CoreUtil.IfElse(AstUtilities.ExpressionIsAlias(node.Expression()!), SymbolFlags.Alias, SymbolFlags.Property);
            var symbol = DeclareSymbol(AstUtilities.GetExports(container.Symbol()!), container.Symbol(), node, flags, SymbolFlags.All);
            if (node.AsExportAssignment().IsExportEquals)
            {
                // Ensure export assignments have a ValueDeclaration set.
                SetValueDeclaration(symbol, node);
            }
        }
    }

    private void BindJsxAttributes(Node node)
    {
        BindAnonymousDeclaration(node, SymbolFlags.ObjectLiteral, InternalSymbolName.JSXAttributes);
    }

    private void BindJsxAttribute(Node node, SymbolFlags symbolFlags, SymbolFlags symbolExcludes)
    {
        DeclareSymbolAndAddToSymbolTable(node, symbolFlags, symbolExcludes);
    }

    private void SetExportContextFlag(Node node)
    {
        // A declaration source file or ambient module declaration that contains no export declarations (but possibly regular
        // declarations with export modifiers) is an export context in which declarations are implicitly exported.
        if ((node.Flags & NodeFlags.Ambient) != 0 && !HasExportDeclarations(node))
        {
            node.Flags |= NodeFlags.ExportContext;
        }
        else
        {
            node.Flags &= ~NodeFlags.ExportContext;
        }
    }

    private bool HasExportDeclarations(Node node)
    {
        Node[]? statements = null;
        switch (node.Kind)
        {
            case SyntaxKind.SourceFile:
                statements = node.Statements();
                break;
            case SyntaxKind.ModuleDeclaration:
                var body = node.Body();
                if (body != null && NodeTests.IsModuleBlock(body))
                {
                    statements = body.Statements();
                }
                break;
        }
        return CoreUtil.Some(statements, static s =>
            NodeTests.IsExportDeclaration(s) || NodeTests.IsExportAssignment(s));
    }

    private void BindFunctionExpression(Node node)
    {
        if (!file.IsDeclarationFile && (node.Flags & NodeFlags.Ambient) == 0 && AstUtilities.IsAsyncFunction(node))
        {
            emitFlags |= NodeFlags.HasAsyncFunctions;
        }
        SetFlowNode(node, currentFlow);
        var bindingName = InternalSymbolName.Function;
        if (NodeTests.IsFunctionExpression(node) && node.AsFunctionExpression().Name() != null)
        {
            CheckStrictModeFunctionName(node);
            bindingName = node.AsFunctionExpression().Name()!.Text();
        }
        BindAnonymousDeclaration(node, SymbolFlags.Function, bindingName);
    }

    private void BindCallExpression(Node node)
    {
        // We're only inspecting call expressions to detect CommonJS modules, so we can skip
        // this check if we've already seen the module indicator
        if (file.CommonJSModuleIndicator == null && AstUtilities.IsRequireCall(node, false /*requireStringLiteralLikeArgument*/))
        {
            SetCommonJSModuleIndicator(node);
        }
    }

    private bool SetCommonJSModuleIndicator(Node node)
    {
        if (file.ExternalModuleIndicator != null && file.ExternalModuleIndicator != file.AsNode())
        {
            return false;
        }
        if (file.CommonJSModuleIndicator == null)
        {
            file.CommonJSModuleIndicator = node;
            if (file.ExternalModuleIndicator == null)
            {
                BindSourceFileAsExternalModule();
            }
        }
        return true;
    }

    private void BindClassLikeDeclaration(Node node)
    {
        var name = node.Name();
        switch (node.Kind)
        {
            case SyntaxKind.ClassDeclaration:
                BindBlockScopedDeclaration(node, SymbolFlags.Class, SymbolFlags.ClassExcludes);
                break;
            case SyntaxKind.ClassExpression:
                var nameText = InternalSymbolName.Class;
                if (name != null)
                {
                    nameText = name.Text();
                    classifiableNames.Add(nameText);
                }
                BindAnonymousDeclaration(node, SymbolFlags.Class, nameText);
                break;
        }
        var symbol = node.Symbol()!;
        // TypeScript 1.0 spec (April 2014): 8.4
        // Every class automatically contains a static property member named 'prototype', the
        // type of which is an instantiation of the class type with type Any supplied as a type
        // argument for each type parameter. It is an error to explicitly declare a static
        // property member with the name 'prototype'.
        //
        // Note: we check for this here because this class may be merging into a module.  The
        // module might have an exported variable called 'prototype'.  We can't allow that as
        // that would clash with the built-in 'prototype' for the class.
        var prototypeSymbol = NewSymbol(SymbolFlags.Property | SymbolFlags.Prototype, "prototype");
        var symbolExport = AstUtilities.GetExports(symbol).GetValueOrDefault(prototypeSymbol.Name);
        if (symbolExport != null)
        {
            ErrorOnNode(symbolExport.Declarations![0], Diag.Duplicate_identifier_0, AstUtilities.SymbolName(prototypeSymbol));
        }
        AstUtilities.GetExports(symbol)[prototypeSymbol.Name] = prototypeSymbol;
        prototypeSymbol.Parent = symbol;
    }

    private void BindPropertyOrMethodOrAccessor(Node node, SymbolFlags symbolFlags, SymbolFlags symbolExcludes)
    {
        if (!file.IsDeclarationFile && (node.Flags & NodeFlags.Ambient) == 0 && AstUtilities.IsAsyncFunction(node))
        {
            emitFlags |= NodeFlags.HasAsyncFunctions;
        }
        if (currentFlow != null && AstUtilities.IsObjectLiteralOrClassExpressionMethodOrAccessor(node))
        {
            SetFlowNode(node, currentFlow);
        }
        if (AstUtilities.HasDynamicName(node))
        {
            BindAnonymousDeclaration(node, symbolFlags, InternalSymbolName.Computed);
        }
        else
        {
            DeclareSymbolAndAddToSymbolTable(node, symbolFlags, symbolExcludes);
        }
    }

    private void BindFunctionOrConstructorType(Node node)
    {
        // For a given function symbol "<...>(...) => T" we want to generate a symbol identical
        // to the one we would get for: { <...>(...): T }
        //
        // We do that by making an anonymous type literal symbol, and then setting the function
        // symbol as its sole member. To the rest of the system, this symbol will be indistinguishable
        // from an actual type literal symbol you would have gotten had you used the long form.
        var symbol = NewSymbol(SymbolFlags.Signature, GetDeclarationName(node));
        AddDeclarationToSymbol(symbol, node, SymbolFlags.Signature);
        var typeLiteralSymbol = NewSymbol(SymbolFlags.TypeLiteral, InternalSymbolName.Type);
        AddDeclarationToSymbol(typeLiteralSymbol, node, SymbolFlags.TypeLiteral);
        typeLiteralSymbol.Members = new SymbolTable();
        typeLiteralSymbol.Members[symbol.Name] = symbol;
    }

    private void AddLateBoundAssignmentDeclarationToSymbol(Node node, Symbol symbol)
    {
        var exports = AstUtilities.GetExports(symbol);
        var assignmentSymbol = exports.GetValueOrDefault(InternalSymbolName.AssignmentDeclaration);
        if (assignmentSymbol == null)
        {
            assignmentSymbol = NewSymbol(SymbolFlags.None, InternalSymbolName.AssignmentDeclaration);
            exports[InternalSymbolName.AssignmentDeclaration] = assignmentSymbol;
        }
        assignmentSymbol.Declarations = [.. (assignmentSymbol.Declarations ?? []), node];
    }

    private void BindModuleExportsAssignment(Node node)
    {
        if (SetCommonJSModuleIndicator(node))
        {
            var container = file.AsNode();
            var flags = CoreUtil.IfElse(AstUtilities.ExpressionIsAlias(node.AsBinaryExpression().Right!), SymbolFlags.Alias, SymbolFlags.Property);
            var symbol = DeclareSymbol(AstUtilities.GetExports(container.Symbol()!), container.Symbol(), node, flags, 0);
            SetValueDeclaration(symbol, node);
        }
    }

    private void BindExpandoPropertyAssignment(Node node)
    {
        expandoAssignments.Add(new ExpandoAssignmentInfo(node, container!, blockScopeContainer!));
    }

    private void BindDeferredExpandoAssignments()
    {
        // map-iter: 已排序 —— expandoAssignments is a List, iterated in insertion (declaration) order.
        foreach (var info in expandoAssignments)
        {
            container = info.Container;
            blockScopeContainer = info.BlockScopeContainer;
            BindDeferredExpandoAssignment(info.Node);
        }
    }

    // If the given module symbol has an export= symbol, promote exports with a type or namespace meaning
    // from the module symbol onto the export= symbol and, if any such exports exist, mark the export=
    // symbol as a namespace module.
    private void BindCommonJSTypeExports(Symbol moduleSymbol)
    {
        var moduleExports = moduleSymbol.Exports;
        var exportEquals = moduleExports?.GetValueOrDefault(InternalSymbolName.ExportEquals);
        if (exportEquals != null)
        {
            // map-iter: 序无关 —— promotes matching exports onto exportEquals, order-independent.
            foreach (var symbol in moduleExports!.Values)
            {
                if (symbol.Name != InternalSymbolName.ExportEquals && (symbol.Flags & (SymbolFlags.Type | SymbolFlags.Namespace)) != 0)
                {
                    AstUtilities.GetExports(exportEquals)[symbol.Name] = symbol;
                    exportEquals.Flags |= SymbolFlags.NamespaceModule;
                }
            }
        }
    }

    private void BindDeferredExpandoAssignment(Node node)
    {
        var parent = GetParentOfPropertyAssignment(node);
        var symbol = LookupEntity(parent, blockScopeContainer!);
        if (symbol == null)
        {
            symbol = LookupEntity(parent, container!);
        }
        if ((symbol = GetInitializerSymbol(symbol)) != null)
        {
            if (AstUtilities.HasDynamicName(node))
            {
                BindAnonymousDeclaration(node, SymbolFlags.Property | SymbolFlags.Assignment, InternalSymbolName.Computed);
                AddLateBoundAssignmentDeclarationToSymbol(node, symbol);
            }
            else
            {
                // We declare expandos only when there are no non-expando declarations for that name.
                var exports = AstUtilities.GetExports(symbol);
                var existing = exports.GetValueOrDefault(GetDeclarationName(node));
                if (existing == null || (existing.Flags & SymbolFlags.Assignment) != 0)
                {
                    DeclareSymbol(exports, symbol, node, SymbolFlags.Property | SymbolFlags.Assignment, SymbolFlags.PropertyExcludes);
                }
            }
        }
    }

    private static Node GetParentOfPropertyAssignment(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.BinaryExpression:
                return node.AsBinaryExpression().Left!.Expression()!;
            case SyntaxKind.CallExpression:
                return node.Arguments()![0];
        }
        throw new InvalidOperationException("Unhandled case in getParentOfPropertyAssignment");
    }

    private void BindExportsOrObjectDefineProperty(Node node)
    {
        if (SetCommonJSModuleIndicator(node))
        {
            var container = file.AsNode();
            var flags = CoreUtil.IfElse(NodeTests.IsBinaryExpression(node) && AstUtilities.ExpressionIsAlias(node.AsBinaryExpression().Right!), SymbolFlags.Alias, SymbolFlags.FunctionScopedVariable);
            DeclareSymbol(AstUtilities.GetExports(container.Symbol()!), container.Symbol(), node, flags, SymbolFlags.FunctionScopedVariableExcludes);
        }
    }

    private static Symbol? GetInitializerSymbol(Symbol? symbol)
    {
        if (symbol == null || symbol.ValueDeclaration == null)
        {
            return null;
        }
        var declaration = symbol.ValueDeclaration;
        // For an assignment 'fn.xxx = ...', where 'fn' is a previously declared function or a previously
        // declared const variable initialized with a function expression or arrow function, we add expando
        // property declarations to the function's symbol. This also applies to class expressions in JS files,
        // and empty object literals in JS files when the declaration doesn't have a type annotation.
        if (NodeTests.IsFunctionDeclaration(declaration) || AstUtilities.IsInJSFile(declaration) && NodeTests.IsClassDeclaration(declaration))
        {
            return symbol;
        }
        else if (NodeTests.IsVariableDeclaration(declaration) &&
            ((declaration.Parent!.Flags & NodeFlags.Const) != 0 || AstUtilities.IsInJSFile(declaration)))
        {
            var initializer = declaration.Initializer();
            if (AstUtilities.IsExpandoInitializer(declaration, initializer!))
            {
                return initializer!.Symbol();
            }
        }
        else if (NodeTests.IsBinaryExpression(declaration) && AstUtilities.IsInJSFile(declaration))
        {
            var initializer = declaration.AsBinaryExpression().Right;
            if (AstUtilities.IsExpandoInitializer(declaration, initializer!))
            {
                return initializer!.Symbol();
            }
        }
        return null;
    }

    private void BindThisPropertyAssignment(Node node)
    {
        if (!AstUtilities.IsInJSFile(node))
        {
            return;
        }
        var bin = node.AsBinaryExpression();
        if (NodeTests.IsPropertyAccessExpression(bin.Left!) && NodeTests.IsPrivateIdentifier(bin.Left!.AsPropertyAccessExpression().Name()!) ||
            thisContainer == null)
        {
            return;
        }
        var (classSymbol, symbolTable) = GetThisClassAndSymbolTable();
        if (symbolTable != null)
        {
            if (AstUtilities.HasDynamicName(node))
            {
                DeclareSymbolEx(symbolTable, classSymbol, node, SymbolFlags.Property, SymbolFlags.None, true /*isReplaceableByMethod*/, true /*isComputedName*/);
                AddLateBoundAssignmentDeclarationToSymbol(node, classSymbol!);
            }
            else
            {
                DeclareSymbolEx(symbolTable, classSymbol, node, SymbolFlags.Property | SymbolFlags.Assignment, SymbolFlags.None, true /*isReplaceableByMethod*/, false /*isComputedName*/);
            }
        }
        else if (thisContainer!.Kind != SyntaxKind.FunctionDeclaration && thisContainer!.Kind != SyntaxKind.FunctionExpression)
        {
            // !!! constructor functions
            throw new InvalidOperationException("Unhandled case in bindThisPropertyAssignment: " + thisContainer!.Kind.ToString());
        }
    }

    private (Symbol? ClassSymbol, SymbolTable? SymbolTable) GetThisClassAndSymbolTable()
    {
        Symbol? classSymbol = null;
        SymbolTable? symbolTable = null;
        if (thisContainer == null)
        {
            return (null, null);
        }
        switch (thisContainer.Kind)
        {
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
                // !!! constructor functions
                break;
            case SyntaxKind.Constructor:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.ClassStaticBlockDeclaration:
                // this.property assignment in class member -- bind to the containing class
                classSymbol = thisContainer.Parent!.Symbol();
                if (AstUtilities.IsStatic(thisContainer))
                {
                    symbolTable = AstUtilities.GetExports(classSymbol!);
                }
                else
                {
                    symbolTable = AstUtilities.GetMembers(classSymbol!);
                }
                break;
        }
        return (classSymbol, symbolTable);
    }

    private void BindEnumDeclaration(Node node)
    {
        if (AstUtilities.IsEnumConst(node))
        {
            BindBlockScopedDeclaration(node, SymbolFlags.ConstEnum, SymbolFlags.ConstEnumExcludes);
        }
        else
        {
            BindBlockScopedDeclaration(node, SymbolFlags.RegularEnum, SymbolFlags.RegularEnumExcludes);
        }
    }

    private void BindVariableDeclarationOrBindingElement(Node node)
    {
        CheckStrictModeEvalOrArguments(node, node.Name());
        var name = node.Name();
        if (name != null && !AstUtilities.IsBindingPattern(name))
        {
            if (AstUtilities.IsVariableDeclarationInitializedToRequire(node))
            {
                DeclareSymbolAndAddToSymbolTable(node, SymbolFlags.Alias, SymbolFlags.AliasExcludes);
            }
            else if (AstUtilities.IsBlockOrCatchScoped(node))
            {
                BindBlockScopedDeclaration(node, SymbolFlags.BlockScopedVariable, SymbolFlags.BlockScopedVariableExcludes);
            }
            else if (AstUtilities.IsPartOfParameterDeclaration(node))
            {
                // It is safe to walk up parent chain to find whether the node is a destructuring parameter declaration
                // because its parent chain has already been set up, since parents are set before descending into children.
                //
                // If node is a binding element in parameter declaration, we need to use ParameterExcludes.
                // Using ParameterExcludes flag allows the compiler to report an error on duplicate identifiers in Parameter Declaration
                // For example:
                //      function foo([a,a]) {} // Duplicate Identifier error
                //      function bar(a,a) {}   // Duplicate Identifier error, parameter declaration in this case is handled in bindParameter
                //                             // which correctly set excluded symbols
                DeclareSymbolAndAddToSymbolTable(node, SymbolFlags.FunctionScopedVariable, SymbolFlags.ParameterExcludes);
            }
            else
            {
                DeclareSymbolAndAddToSymbolTable(node, SymbolFlags.FunctionScopedVariable, SymbolFlags.FunctionScopedVariableExcludes);
            }
        }
    }

    private void BindParameter(Node node)
    {
        var decl = node.AsParameterDeclaration();
        if ((node.Flags & NodeFlags.Ambient) == 0)
        {
            // It is a SyntaxError if the identifier eval or arguments appears within a FormalParameterList of a
            // strict mode FunctionLikeDeclaration or FunctionExpression(13.1)
            CheckStrictModeEvalOrArguments(node, decl.Name());
        }
        if (AstUtilities.IsBindingPattern(decl.Name()))
        {
            var index = Array.IndexOf(node.Parent!.Parameters(), node);
            BindAnonymousDeclaration(node, SymbolFlags.FunctionScopedVariable, "__" + index.ToString());
        }
        else
        {
            DeclareSymbolAndAddToSymbolTable(node, SymbolFlags.FunctionScopedVariable, SymbolFlags.ParameterExcludes);
        }
        // If this is a property-parameter, then also declare the property symbol into the
        // containing class.
        if (AstUtilities.IsParameterPropertyDeclaration(node, node.Parent!))
        {
            var classDeclaration = node.Parent!.Parent!;
            var flags = SymbolFlags.Property | CoreUtil.IfElse(decl.QuestionToken != null, SymbolFlags.Optional, SymbolFlags.None);
            DeclareSymbol(AstUtilities.GetMembers(classDeclaration.Symbol()!), classDeclaration.Symbol(), node, flags, SymbolFlags.PropertyExcludes);
        }
    }

    private void BindFunctionDeclaration(Node node)
    {
        if (!file.IsDeclarationFile && (node.Flags & NodeFlags.Ambient) == 0 && AstUtilities.IsAsyncFunction(node))
        {
            emitFlags |= NodeFlags.HasAsyncFunctions;
        }
        CheckStrictModeFunctionName(node);
        BindBlockScopedDeclaration(node, SymbolFlags.Function, SymbolFlags.FunctionExcludes);
    }

    private Node? GetInferTypeContainer(Node node)
    {
        var extendsType = AstUtilities.FindAncestor(node, static n =>
        {
            var parent = n.Parent;
            return parent != null && NodeTests.IsConditionalTypeNode(parent) && parent.AsConditionalTypeNode().ExtendsType == n;
        });
        if (extendsType != null)
        {
            return extendsType.Parent;
        }
        return null;
    }

    private void BindAnonymousDeclaration(Node node, SymbolFlags symbolFlags, string name)
    {
        var symbol = NewSymbol(symbolFlags, name);
        if ((symbolFlags & (SymbolFlags.EnumMember | SymbolFlags.ClassMember)) != 0)
        {
            symbol.Parent = container!.Symbol();
        }
        AddDeclarationToSymbol(symbol, node, symbolFlags);
    }

    private void BindBlockScopedDeclaration(Node node, SymbolFlags symbolFlags, SymbolFlags symbolExcludes)
    {
        switch (blockScopeContainer!.Kind)
        {
            case SyntaxKind.ModuleDeclaration:
                DeclareModuleMember(node, symbolFlags, symbolExcludes);
                break;
            case SyntaxKind.SourceFile:
                if (AstUtilities.IsExternalOrCommonJSModule(container!.AsSourceFile()))
                {
                    DeclareModuleMember(node, symbolFlags, symbolExcludes);
                    break;
                }
                goto default;
            default:
                DeclareSymbol(AstUtilities.GetLocals(blockScopeContainer), null /*parent*/, node, symbolFlags, symbolExcludes);
                break;
        }
    }

    private void BindTypeParameter(Node node)
    {
        if (node.Parent!.Kind == SyntaxKind.InferType)
        {
            var container = GetInferTypeContainer(node.Parent);
            if (container != null)
            {
                DeclareSymbol(AstUtilities.GetLocals(container), null /*parent*/, node, SymbolFlags.TypeParameter, SymbolFlags.TypeParameterExcludes);
            }
            else
            {
                BindAnonymousDeclaration(node, SymbolFlags.TypeParameter, GetDeclarationName(node));
            }
        }
        else
        {
            DeclareSymbolAndAddToSymbolTable(node, SymbolFlags.TypeParameter, SymbolFlags.TypeParameterExcludes);
        }
    }

    private Symbol? LookupEntity(Node node, Node container)
    {
        if (NodeTests.IsIdentifier(node))
        {
            return LookupName(node.Text(), container);
        }
        if (node.Expression()!.Kind == SyntaxKind.ThisKeyword)
        {
            var (_, symbolTable) = GetThisClassAndSymbolTable();
            if (symbolTable != null)
            {
                var name = AstUtilities.GetElementOrPropertyAccessName(node);
                if (name != null)
                {
                    return symbolTable.GetValueOrDefault(name.Text());
                }
            }
            return null;
        }
        var symbol = GetInitializerSymbol(LookupEntity(node.Expression()!, container));
        if (symbol != null && symbol.Exports != null)
        {
            var name = AstUtilities.GetElementOrPropertyAccessName(node);
            if (name != null)
            {
                return symbol.Exports.GetValueOrDefault(name.Text());
            }
        }
        return null;
    }

    private Symbol? LookupName(string name, Node container)
    {
        var localsContainer = container.LocalsContainerData();
        if (localsContainer != null)
        {
            var local = localsContainer.Locals?.GetValueOrDefault(name);
            if (local != null)
            {
                return CoreUtil.OrElse(local.ExportSymbol, local);
            }
        }
        var declaration = container.DeclarationData();
        if (declaration != null && declaration.Symbol != null)
        {
            return declaration.Symbol.Exports?.GetValueOrDefault(name);
        }
        return null;
    }

    // The binder visits every node in the syntax tree so it is a convenient place to perform a single localized
    // check for reserved words used as identifiers in strict mode code, as well as `yield` or `await` in
    // [Yield] or [Await] contexts, respectively.
    private void CheckContextualIdentifier(Node node)
    {
        // Report error only if there are no parse errors in file
        if ((file.Diagnostics()?.Length ?? 0) == 0 && (node.Flags & NodeFlags.Ambient) == 0 && (node.Flags & NodeFlags.JSDoc) == 0 && !AstUtilities.IsIdentifierName(node))
        {
            // strict mode identifiers
            var originalKeywordKind = global::Ts.Scanner.Scanner.GetIdentifierToken(node.Text());
            if (originalKeywordKind == SyntaxKind.Identifier)
            {
                return;
            }
            if (originalKeywordKind >= SyntaxKind.FirstFutureReservedWord && originalKeywordKind <= SyntaxKind.LastFutureReservedWord)
            {
                ErrorOnNode(node, GetStrictModeIdentifierMessage(node), global::Ts.Scanner.Scanner.DeclarationNameToString(node));
            }
            else if (originalKeywordKind == SyntaxKind.AwaitKeyword)
            {
                if (AstUtilities.IsExternalModule(file) && AstUtilities.IsInTopLevelContext(node))
                {
                    ErrorOnNode(node, Diag.Identifier_expected_0_is_a_reserved_word_at_the_top_level_of_a_module, global::Ts.Scanner.Scanner.DeclarationNameToString(node));
                }
                else if ((node.Flags & NodeFlags.AwaitContext) != 0)
                {
                    ErrorOnNode(node, Diag.Identifier_expected_0_is_a_reserved_word_that_cannot_be_used_here, global::Ts.Scanner.Scanner.DeclarationNameToString(node));
                }
            }
            else if (originalKeywordKind == SyntaxKind.YieldKeyword && (node.Flags & NodeFlags.YieldContext) != 0)
            {
                ErrorOnNode(node, Diag.Identifier_expected_0_is_a_reserved_word_that_cannot_be_used_here, global::Ts.Scanner.Scanner.DeclarationNameToString(node));
            }
        }
    }

    private void CheckPrivateIdentifier(Node node)
    {
        if (node.Text() == "#constructor")
        {
            // Report error only if there are no parse errors in file
            if ((file.Diagnostics()?.Length ?? 0) == 0)
            {
                ErrorOnNode(node, Diag.X_constructor_is_a_reserved_word, global::Ts.Scanner.Scanner.DeclarationNameToString(node));
            }
        }
    }

    private Message GetStrictModeIdentifierMessage(Node node)
    {
        // Provide specialized messages to help the user understand why we think they're in
        // strict mode.
        if (AstUtilities.GetContainingClass(node) != null)
        {
            return Diag.Identifier_expected_0_is_a_reserved_word_in_strict_mode_Class_definitions_are_automatically_in_strict_mode;
        }
        if (file.ExternalModuleIndicator != null)
        {
            return Diag.Identifier_expected_0_is_a_reserved_word_in_strict_mode_Modules_are_automatically_in_strict_mode;
        }
        return Diag.Identifier_expected_0_is_a_reserved_word_in_strict_mode;
    }

    // Should be called only on prologue directives (ast.IsPrologueDirective(node) should be true)
    private static bool IsUseStrictPrologueDirective(SourceFile sourceFile, Node node)
    {
        var nodeText = global::Ts.Scanner.Scanner.GetSourceTextOfNodeFromSourceFile(sourceFile, node.Expression()!, false /*includeTrivia*/);
        // Note: the node text must be exactly "use strict" or 'use strict'.  It is not ok for the
        // string to contain unicode escapes (as per ES5).
        return nodeText == "\"use strict\"" || nodeText == "'use strict'";
    }

    public static Node? FindUseStrictPrologue(SourceFile sourceFile, Node[] statements)
    {
        foreach (var statement in statements)
        {
            if (AstUtilities.IsPrologueDirective(statement))
            {
                if (IsUseStrictPrologueDirective(sourceFile, statement))
                {
                    return statement;
                }
            }
            else
            {
                return null;
            }
        }

        return null;
    }

    private void CheckStrictModeFunctionName(Node node)
    {
        if ((node.Flags & NodeFlags.Ambient) == 0)
        {
            // It is a SyntaxError if the identifier eval or arguments appears within a FormalParameterList of a strict mode FunctionDeclaration or FunctionExpression (13.1))
            CheckStrictModeEvalOrArguments(node, node.Name());
        }
    }

    private void CheckStrictModeBinaryExpression(Node node)
    {
        var expr = node.AsBinaryExpression();
        if (AstUtilities.IsLeftHandSideExpression(expr.Left!) && NodeTests.IsAssignmentOperator(expr.OperatorToken!.Kind))
        {
            // ECMA 262 (Annex C) The identifier eval or arguments may not appear as the LeftHandSideExpression of an
            // Assignment operator(11.13) or of a PostfixExpression(11.3)
            CheckStrictModeEvalOrArguments(node, expr.Left);
        }
    }

    private void CheckStrictModeCatchClause(Node node)
    {
        // It is a SyntaxError if a TryStatement with a Catch occurs within strict code and the Identifier of the
        // Catch production is eval or arguments
        var clause = node.AsCatchClause();
        if (clause.VariableDeclaration != null)
        {
            CheckStrictModeEvalOrArguments(node, clause.VariableDeclaration.AsVariableDeclaration().Name());
        }
    }

    private void CheckStrictModeDeleteExpression(Node node)
    {
        // Grammar checking
        var expr = node.AsDeleteExpression();
        if (expr.Expression!.Kind == SyntaxKind.Identifier)
        {
            // When a delete operator occurs within strict mode code, a SyntaxError is thrown if its
            // UnaryExpression is a direct reference to a variable, function argument, or function name
            ErrorOnNode(expr.Expression, Diag.X_delete_cannot_be_called_on_an_identifier_in_strict_mode);
        }
    }

    private void CheckStrictModePostfixUnaryExpression(Node node)
    {
        // Grammar checking
        // The identifier eval or arguments may not appear as the LeftHandSideExpression of an
        // Assignment operator(11.13) or of a PostfixExpression(11.3) or as the UnaryExpression
        // operated upon by a Prefix Increment(11.4.4) or a Prefix Decrement(11.4.5) operator.
        CheckStrictModeEvalOrArguments(node, node.AsPostfixUnaryExpression().Operand);
    }

    private void CheckStrictModePrefixUnaryExpression(Node node)
    {
        // Grammar checking
        var expr = node.AsPrefixUnaryExpression();
        if (expr.Operator == SyntaxKind.PlusPlusToken || expr.Operator == SyntaxKind.MinusMinusToken)
        {
            CheckStrictModeEvalOrArguments(node, expr.Operand);
        }
    }

    private void CheckStrictModeWithStatement(Node node)
    {
        // Grammar checking for withStatement
        ErrorOnFirstToken(node, Diag.X_with_statements_are_not_allowed_in_strict_mode);
    }

    private void CheckStrictModeLabeledStatement(Node node)
    {
        // Grammar checking for labeledStatement
        var data = node.AsLabeledStatement();
        if (AstUtilities.IsDeclarationStatement(data.Statement!) || NodeTests.IsVariableStatement(data.Statement!))
        {
            ErrorOnFirstToken(data.Label!, Diag.A_label_is_not_allowed_here);
        }
    }

    private static bool IsEvalOrArgumentsIdentifier(Node node)
    {
        if (NodeTests.IsIdentifier(node))
        {
            var text = node.Text();
            return text == "eval" || text == "arguments";
        }
        return false;
    }

    private void CheckStrictModeEvalOrArguments(Node contextNode, Node? name)
    {
        if (name != null && IsEvalOrArgumentsIdentifier(name))
        {
            // We check first if the name is inside class declaration or class expression; if so give explicit message
            // otherwise report generic error message.
            ErrorOnNode(name, GetStrictModeEvalOrArgumentsMessage(contextNode), name.Text());
        }
    }

    private Message GetStrictModeEvalOrArgumentsMessage(Node node)
    {
        // Provide specialized messages to help the user understand why we think they're in strict mode
        if (AstUtilities.GetContainingClass(node) != null)
        {
            return Diag.Code_contained_in_a_class_is_evaluated_in_JavaScript_s_strict_mode_which_does_not_allow_this_use_of_0_For_more_information_see_https_Colon_Slash_Slashdeveloper_mozilla_org_Slashen_US_Slashdocs_SlashWeb_SlashJavaScript_SlashReference_SlashStrict_mode;
        }
        if (file.ExternalModuleIndicator != null)
        {
            return Diag.Invalid_use_of_0_Modules_are_automatically_in_strict_mode;
        }
        return Diag.Invalid_use_of_0_in_strict_mode;
    }
}
