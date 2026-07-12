using System;
using System.Collections.Generic;
using Ts.Core;
using static Ts.Ast.NodeTests;

namespace Ts.Ast;

// Go: internal/ast/utilities.go (and a few ast.go helpers) — the binder-facing slice of the
// ast utility surface, deferred from the phase-3 Utilities.cs port (see its SCOPE note) and
// landed here for the phase-4 binder. Ported 逐函数直译 in Go source order; Go comments and
// `// !!!` markers are kept verbatim. Guards (IsIdentifier, IsPropertyAccessExpression, …)
// resolve to Ts.Ast.NodeTests via `using static`; the composite predicates and node accessors
// already ported live in AstUtilities (Utilities.cs) / NodeAccessors and are called unqualified.
// Go implicit non-nil derefs become `!` (NRE ↔ Go panic, plan/05 §4.3).
public static partial class AstUtilities
{
    // Go: ast.go:1512-1514
    public static bool IsDeclarationNode(Node node)
    {
        return node.DeclarationData() != null;
    }

    // Go: ast.go:1529-1531
    public static bool IsLocalsContainer(Node node)
    {
        return node.LocalsContainerData() != null;
    }

    // Go: utilities.go:1279-1284
    public static bool IsDeclaration(Node node)
    {
        if (node.Kind == SyntaxKind.TypeParameter)
        {
            return node.Parent != null;
        }
        return IsDeclarationNode(node);
    }

    // Go: utilities.go:1311-1313
    public static bool IsModuleOrEnumDeclaration(Node node)
    {
        return node.Kind == SyntaxKind.ModuleDeclaration || node.Kind == SyntaxKind.EnumDeclaration;
    }

    // Go: utilities.go:1132-1134
    public static bool IsFunctionExpressionOrArrowFunction(Node node)
    {
        return IsFunctionExpression(node) || IsArrowFunction(node);
    }

    // Go: utilities.go:1336-1338
    public static bool IsExportsIdentifier(Node node)
    {
        return IsIdentifier(node) && node.Text() == "exports";
    }

    // Go: utilities.go:1340-1342
    public static bool IsModuleIdentifier(Node node)
    {
        return IsIdentifier(node) && node.Text() == "module";
    }

    // Go: utilities.go:1352-1356
    public static bool IsBindableStaticAccessExpression(Node node, bool excludeThisKeyword)
    {
        return IsPropertyAccessExpression(node) &&
            (!excludeThisKeyword && node.Expression()!.Kind == SyntaxKind.ThisKeyword || IsIdentifier(node.Name()!) && IsBindableStaticNameExpression(node.Expression()!, true /*excludeThisKeyword*/)) ||
            IsBindableStaticElementAccessExpression(node, excludeThisKeyword);
    }

    // Go: utilities.go:1358-1363
    public static bool IsBindableStaticElementAccessExpression(Node node, bool excludeThisKeyword)
    {
        return IsLiteralLikeElementAccess(node) &&
            ((!excludeThisKeyword && node.Expression()!.Kind == SyntaxKind.ThisKeyword) ||
                IsEntityNameExpression(node.Expression()!) ||
                IsBindableStaticAccessExpression(node.Expression()!, true /*excludeThisKeyword*/));
    }

    // Go: utilities.go:1374-1376
    public static bool IsLiteralLikeElementAccess(Node node)
    {
        return IsElementAccessExpression(node) && IsStringOrNumericLiteralLike(node.AsElementAccessExpression().ArgumentExpression!);
    }

    // Go: utilities.go:1378-1380
    public static bool IsBindableStaticNameExpression(Node node, bool excludeThisKeyword)
    {
        return IsEntityNameExpression(node) || IsBindableStaticAccessExpression(node, excludeThisKeyword);
    }

    // Does not handle signed numeric names like `a[+0]` - handling those would require handling prefix unary expressions
    // throughout late binding handling as well, which is awkward (but ultimately probably doable if there is demand)
    // Go: utilities.go:1384-1398
    public static Node? GetElementOrPropertyAccessName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PropertyAccessExpression:
                if (IsIdentifier(node.Name()!))
                {
                    return node.Name();
                }
                return null;
            case SyntaxKind.ElementAccessExpression:
            {
                var arg = SkipParentheses(node.AsElementAccessExpression().ArgumentExpression!);
                if (IsStringOrNumericLiteralLike(arg))
                {
                    return arg;
                }
                return null;
            }
        }
        throw new InvalidOperationException("Unhandled case in GetElementOrPropertyAccessName");
    }

    // Go: utilities.go:3718-3720
    public static bool IsCallOrNewExpression(Node node)
    {
        return IsCallExpression(node) || IsNewExpression(node);
    }

    // Go: utilities.go:3703-3716
    public static Node? GetInvokedExpression(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.TaggedTemplateExpression:
                return node.AsTaggedTemplateExpression().Tag;
            case SyntaxKind.JsxOpeningElement:
            case SyntaxKind.JsxSelfClosingElement:
                return node.TagName();
            case SyntaxKind.BinaryExpression:
                return node.AsBinaryExpression().Right;
            case SyntaxKind.JsxOpeningFragment:
                return node;
            default:
                return node.Expression();
        }
    }

    // Go: utilities.go:1365-1372
    public static bool IsPrototypeAccess(Node node)
    {
        if (IsBindableStaticAccessExpression(node, false /*excludeThisKeyword*/))
        {
            var name = GetElementOrPropertyAccessName(node);
            if (name != null)
            {
                return name.Text() == "prototype";
            }
        }
        return false;
    }

    // Go: utilities.go:2870-2872
    public static bool IsPlainJSFile(SourceFile? file, Tristate checkJs)
    {
        return file != null && (file.ScriptKind == ScriptKind.JS || file.ScriptKind == ScriptKind.Jsx) && file.CheckJsDirective == null && checkJs.IsUnknown();
    }

    // Go: utilities.go:1428-1440
    public static Node? GetNameOfDeclaration(Node? declaration)
    {
        if (declaration == null)
        {
            return null;
        }
        var nonAssignedName = GetNonAssignedNameOfDeclaration(declaration);
        if (nonAssignedName != null)
        {
            return nonAssignedName;
        }
        if (IsFunctionExpression(declaration) || IsArrowFunction(declaration) || IsClassExpression(declaration))
        {
            return GetAssignedName(declaration);
        }
        return null;
    }

    // Go: utilities.go:1442-1465
    public static Node? GetNonAssignedNameOfDeclaration(Node declaration)
    {
        // !!!
        switch (declaration.Kind)
        {
            case SyntaxKind.BinaryExpression:
            case SyntaxKind.CallExpression:
                switch (GetAssignmentDeclarationKind(declaration))
                {
                    case JSDeclarationKind.Property:
                    case JSDeclarationKind.ThisProperty:
                    case JSDeclarationKind.ExportsProperty:
                    {
                        var left = declaration.AsBinaryExpression().Left;
                        var name = GetElementOrPropertyAccessName(left!);
                        if (name != null)
                        {
                            return name;
                        }
                        return left;
                    }
                    case JSDeclarationKind.ObjectDefinePropertyValue:
                    case JSDeclarationKind.ObjectDefinePropertyExports:
                        return declaration.Arguments()![1];
                }
                return null;
            case SyntaxKind.ExportAssignment:
            {
                var expr = declaration.Expression();
                if (IsIdentifier(expr!))
                {
                    return expr;
                }
                return null;
            }
        }
        return declaration.Name();
    }

    // Go: utilities.go:1467-1498
    public static Node? GetAssignedName(Node node)
    {
        var parent = node.Parent;
        if (parent != null)
        {
            switch (parent.Kind)
            {
                case SyntaxKind.PropertyAssignment:
                    return parent.AsPropertyAssignment().Name();
                case SyntaxKind.BindingElement:
                    return parent.AsBindingElement().Name();
                case SyntaxKind.BinaryExpression:
                    if (node == parent.AsBinaryExpression().Right)
                    {
                        var left = parent.AsBinaryExpression().Left;
                        switch (left!.Kind)
                        {
                            case SyntaxKind.Identifier:
                                return left;
                            case SyntaxKind.PropertyAccessExpression:
                                return left.AsPropertyAccessExpression().Name();
                            case SyntaxKind.ElementAccessExpression:
                                var arg = SkipParentheses(left.AsElementAccessExpression().ArgumentExpression!);
                                if (IsStringOrNumericLiteralLike(arg))
                                {
                                    return arg;
                                }
                                break;
                        }
                    }
                    break;
                case SyntaxKind.VariableDeclaration:
                    var name = parent.AsVariableDeclaration().Name();
                    if (IsIdentifier(name!))
                    {
                        return name;
                    }
                    break;
            }
        }
        return null;
    }

    // Go: utilities.go:1522-1554
    public static JSDeclarationKind GetAssignmentDeclarationKind(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.BinaryExpression:
            {
                var bin = node.AsBinaryExpression();
                if (bin.OperatorToken!.Kind == SyntaxKind.EqualsToken && IsAccessExpression(bin.Left!))
                {
                    if (IsInJSFile(bin.Left))
                    {
                        if (IsModuleExportsAccessExpression(bin.Left!) && !IsExportsIdentifier(bin.Right!))
                        {
                            return JSDeclarationKind.ModuleExports;
                        }
                        if ((IsModuleExportsAccessExpression(bin.Left!.Expression()!) || IsExportsIdentifier(bin.Left!.Expression()!)) &&
                            GetElementOrPropertyAccessName(bin.Left!) != null)
                        {
                            return JSDeclarationKind.ExportsProperty;
                        }
                        if (bin.Left!.Expression()!.Kind == SyntaxKind.ThisKeyword)
                        {
                            return JSDeclarationKind.ThisProperty;
                        }
                    }
                    if (bin.Left!.Kind == SyntaxKind.PropertyAccessExpression && IsEntityNameExpressionEx(bin.Left!.Expression()!, IsInJSFile(bin.Left)) && IsIdentifier(bin.Left!.Name()!) ||
                        bin.Left!.Kind == SyntaxKind.ElementAccessExpression && IsEntityNameExpressionEx(bin.Left!.Expression()!, IsInJSFile(bin.Left)))
                    {
                        return JSDeclarationKind.Property;
                    }
                }
                break;
            }
            case SyntaxKind.CallExpression:
                if (IsInJSFile(node) && IsBindableObjectDefinePropertyCall(node))
                {
                    var entityName = node.Arguments()![0];
                    if (IsExportsIdentifier(entityName) || IsModuleExportsAccessExpression(entityName))
                    {
                        return JSDeclarationKind.ObjectDefinePropertyExports;
                    }
                    return JSDeclarationKind.ObjectDefinePropertyValue;
                }
                break;
        }
        return JSDeclarationKind.None;
    }

    // Go: utilities.go:1556-1567
    public static bool IsBindableObjectDefinePropertyCall(Node node)
    {
        var args = node.Arguments();
        if (args != null && args.Length == 3)
        {
            var expr = node.Expression();
            if (IsPropertyAccessExpression(expr!) &&
                IsIdentifier(expr!.Expression()!) && expr!.Expression()!.Text() == "Object" &&
                expr!.Name()!.Text() == "defineProperty" &&
                IsStringOrNumericLiteralLike(args[1]) &&
                IsBindableStaticNameExpression(args[0], true /*excludeThisKeyword*/))
            {
                return true;
            }
        }
        return false;
    }

    // A declaration has a dynamic name if all of the following are true:
    //   1. The declaration has a computed property name.
    //   2. The computed name is *not* expressed as a StringLiteral.
    //   3. The computed name is *not* expressed as a NumericLiteral.
    //   4. The computed name is *not* expressed as a PlusToken or MinusToken
    //      immediately followed by a NumericLiteral.
    // Go: utilities.go:1577-1580
    public static bool HasDynamicName(Node declaration)
    {
        var name = GetNameOfDeclaration(declaration);
        return name != null && IsDynamicName(name);
    }

    // Go: utilities.go:1582-1593
    public static bool IsDynamicName(Node name)
    {
        Node? expr;
        switch (name.Kind)
        {
            case SyntaxKind.ComputedPropertyName:
                expr = name.Expression();
                break;
            case SyntaxKind.ElementAccessExpression:
                expr = SkipParentheses(name.AsElementAccessExpression().ArgumentExpression!);
                break;
            default:
                return false;
        }
        return !IsStringOrNumericLiteralLike(expr!) && !IsSignedNumericLiteral(expr!);
    }

    // Go: utilities.go:1595-1597
    public static bool IsEntityNameExpression(Node node)
    {
        return IsEntityNameExpressionEx(node, false /*allowJS*/);
    }

    // Go: utilities.go:1599-1603
    public static bool IsEntityNameExpressionEx(Node node, bool allowJS)
    {
        return IsIdentifier(node) ||
            IsPropertyAccessEntityNameExpression(node, allowJS) ||
            allowJS && (node.Kind == SyntaxKind.ThisKeyword || IsElementAccessEntityNameExpression(node, allowJS));
    }

    // Go: utilities.go:1605-1607
    public static bool IsPropertyAccessEntityNameExpression(Node node, bool allowJS)
    {
        return IsPropertyAccessExpression(node) && IsIdentifier(node.Name()!) && IsEntityNameExpressionEx(node.Expression()!, allowJS);
    }

    // Go: utilities.go:1609-1611
    private static bool IsElementAccessEntityNameExpression(Node node, bool allowJS)
    {
        return IsElementAccessExpression(node) && IsStringOrNumericLiteralLike(node.AsElementAccessExpression().ArgumentExpression!) && IsEntityNameExpressionEx(node.Expression()!, allowJS);
    }

    // Go: utilities.go:1613-1621
    public static bool IsDottedName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.ThisKeyword:
            case SyntaxKind.SuperKeyword:
            case SyntaxKind.MetaProperty:
                return true;
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ParenthesizedExpression:
                return IsDottedName(node.Expression()!);
        }
        return false;
    }

    // Go: utilities.go:1633-1635
    public static bool IsAmbientModule(Node node)
    {
        return IsModuleDeclaration(node) && (node.AsModuleDeclaration().Name()!.Kind == SyntaxKind.StringLiteral || IsGlobalScopeAugmentation(node));
    }

    // utilities.go:179-182 — The only kind of module that can be missing a body is a shorthand ambient module.
    public static bool IsShorthandAmbientModule(Node? node)
    {
        return node != null && node.Kind == SyntaxKind.ModuleDeclaration && node.Body() == null;
    }

    // Go: utilities.go:1641-1643
    public static bool IsExternalModule(SourceFile file)
    {
        return file.ExternalModuleIndicator != null;
    }

    // Go: utilities.go:1645-1647
    public static bool IsExternalOrCommonJSModule(SourceFile file)
    {
        return file.ExternalModuleIndicator != null || file.CommonJSModuleIndicator != null;
    }

    // Go: utilities.go:1671-1673
    public static bool IsGlobalScopeAugmentation(Node node)
    {
        return IsModuleDeclaration(node) && node.AsModuleDeclaration().Keyword == SyntaxKind.GlobalKeyword;
    }

    // Go: utilities.go:1675-1687
    public static bool IsModuleAugmentationExternal(Node node)
    {
        // external module augmentation is a ambient module declaration that is either:
        // - defined in the top level scope and source file is an external module
        // - defined inside ambient module declaration located in the top level scope and source file not an external module
        switch (node.Parent!.Kind)
        {
            case SyntaxKind.SourceFile:
                return IsExternalModule(node.Parent!.AsSourceFile());
            case SyntaxKind.ModuleBlock:
                var grandParent = node.Parent!.Parent;
                return IsAmbientModule(grandParent!) && IsSourceFile(grandParent!.Parent!) && !IsExternalModule(grandParent.Parent!.AsSourceFile());
        }
        return false;
    }

    // Go: utilities.go:1693-1695
    public static Node? GetContainingClass(Node node)
    {
        return FindAncestor(node.Parent, IsClassLike);
    }

    // Go: utilities.go:1741-1746
    public static bool IsPartOfTypeQuery(Node node)
    {
        while (node.Kind == SyntaxKind.QualifiedName || node.Kind == SyntaxKind.Identifier)
        {
            node = node.Parent!;
        }
        return node.Kind == SyntaxKind.TypeQuery;
    }

    // This function returns true if the this node's root declaration is a parameter.
    // For example, passing a `ParameterDeclaration` will return true, as will passing a
    // binding element that is a child of a `ParameterDeclaration`.
    //
    // If you are looking to test that a `Node` is a `ParameterDeclaration`, use `isParameter`.
    // Go: utilities.go:1755-1757
    public static bool IsPartOfParameterDeclaration(Node node)
    {
        return GetRootDeclaration(node).Kind == SyntaxKind.Parameter;
    }

    // Go: utilities.go:1834-1847
    public static Node? GetImmediatelyInvokedFunctionExpression(Node fn)
    {
        if (IsFunctionExpressionOrArrowFunction(fn))
        {
            var prev = fn;
            var parent = fn.Parent;
            while (IsParenthesizedExpression(parent!))
            {
                prev = parent;
                parent = parent!.Parent;
            }
            if (IsCallExpression(parent!) && parent!.Expression() == prev)
            {
                return parent;
            }
        }
        return null;
    }

    // Go: utilities.go:1849-1851
    public static bool IsEnumConst(Node node)
    {
        return (GetCombinedModifierFlags(node) & ModifierFlags.Const) != 0;
    }

    // Go: utilities.go:1853-1855
    public static bool ExpressionIsAlias(Node node)
    {
        return IsEntityNameExpression(node) || IsClassExpression(node);
    }

    // Go: utilities.go:1876-1878
    public static bool IsJsonSourceFile(SourceFile file)
    {
        return file.ScriptKind == ScriptKind.Json;
    }

    // Go: utilities.go:2280-2282
    private static Node[] PushAncestor(Node[]? ancestors, Node parent)
    {
        // append(ancestors, parent). PORT-NOTE: copy-on-append avoids Go slice backing-array
        // aliasing; observationally identical here (pushed values are deterministic per position).
        var len = ancestors == null ? 0 : ancestors.Length;
        var result = new Node[len + 1];
        if (ancestors != null)
        {
            Array.Copy(ancestors, result, len);
        }
        result[len] = parent;
        return result;
    }

    // If a virtual `Parent` exists on the stack, returns the previous stack entry and the virtual `Parent`.
    // Otherwise, we return `nil` and the value of `node.Parent`.
    // Go: utilities.go:2286-2292
    private static (Node[]? Ancestors, Node? Node) PopAncestor(Node[]? ancestors, Node node)
    {
        if (ancestors == null || ancestors.Length == 0)
        {
            return (null, node.Parent);
        }
        var n = ancestors.Length - 1;
        return (ancestors[..n], ancestors[n]);
    }

    // Go: utilities.go:2424-2428
    public static bool IsInstantiatedModule(Node node, bool preserveConstEnums)
    {
        var moduleState = GetModuleInstanceState(node);
        return moduleState == ModuleInstanceState.Instantiated ||
            (preserveConstEnums && moduleState == ModuleInstanceState.ConstEnumOnly);
    }

    // Go: utilities.go:2303-2305
    public static ModuleInstanceState GetModuleInstanceState(Node node)
    {
        return GetModuleInstanceStateCore(node, null, null);
    }

    // Go: utilities.go:2307-2314 (Go `getModuleInstanceState`; renamed per plan/05 §1.1
    // same-name-conflict rule — the exported wrapper keeps the name, this one gets `Core`).
    private static ModuleInstanceState GetModuleInstanceStateCore(Node node, Node[]? ancestors, Dictionary<NodeId, ModuleInstanceState>? visited)
    {
        var module = node.AsModuleDeclaration();
        if (module.Body != null)
        {
            return GetModuleInstanceStateCached(module.Body, PushAncestor(ancestors, node), visited);
        }
        else
        {
            return ModuleInstanceState.Instantiated;
        }
    }

    // Go: utilities.go:2316-2331
    private static ModuleInstanceState GetModuleInstanceStateCached(Node node, Node[]? ancestors, Dictionary<NodeId, ModuleInstanceState>? visited)
    {
        if (visited == null)
        {
            visited = new Dictionary<NodeId, ModuleInstanceState>();
        }
        var nodeId = GetNodeId(node);
        if (visited.TryGetValue(nodeId, out var cached))
        {
            if (cached != ModuleInstanceState.Unknown)
            {
                return cached;
            }
            return ModuleInstanceState.NonInstantiated;
        }
        visited[nodeId] = ModuleInstanceState.Unknown;
        var result = GetModuleInstanceStateWorker(node, ancestors, visited);
        visited[nodeId] = result;
        return result;
    }

    // Go: utilities.go:2333-2385
    private static ModuleInstanceState GetModuleInstanceStateWorker(Node node, Node[]? ancestors, Dictionary<NodeId, ModuleInstanceState>? visited)
    {
        // A module is uninstantiated if it contains only
        switch (node.Kind)
        {
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                return ModuleInstanceState.NonInstantiated;
            case SyntaxKind.EnumDeclaration:
                if (IsEnumConst(node))
                {
                    return ModuleInstanceState.ConstEnumOnly;
                }
                break;
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
            case SyntaxKind.ImportEqualsDeclaration:
                if (!HasSyntacticModifier(node, ModifierFlags.Export))
                {
                    return ModuleInstanceState.NonInstantiated;
                }
                break;
            case SyntaxKind.ExportDeclaration:
            {
                var decl = node.AsExportDeclaration();
                if (decl.ModuleSpecifier == null && decl.ExportClause != null && decl.ExportClause.Kind == SyntaxKind.NamedExports)
                {
                    var state = ModuleInstanceState.NonInstantiated;
                    ancestors = PushAncestor(ancestors, node);
                    ancestors = PushAncestor(ancestors, decl.ExportClause);
                    foreach (var specifier in decl.ExportClause.Elements() ?? [])
                    {
                        var specifierState = GetModuleInstanceStateForAliasTarget(specifier, ancestors, visited);
                        if (specifierState > state)
                        {
                            state = specifierState;
                        }
                        if (state == ModuleInstanceState.Instantiated)
                        {
                            return state;
                        }
                    }
                    return state;
                }
                break;
            }
            case SyntaxKind.ModuleBlock:
            {
                var state = ModuleInstanceState.NonInstantiated;
                ancestors = PushAncestor(ancestors, node);
                node.ForEachChild(n =>
                {
                    var childState = GetModuleInstanceStateCached(n, ancestors, visited);
                    switch (childState)
                    {
                        case ModuleInstanceState.NonInstantiated:
                            return false;
                        case ModuleInstanceState.ConstEnumOnly:
                            state = ModuleInstanceState.ConstEnumOnly;
                            return false;
                        case ModuleInstanceState.Instantiated:
                            state = ModuleInstanceState.Instantiated;
                            return true;
                    }
                    throw new InvalidOperationException("Unhandled case in getModuleInstanceStateWorker");
                });
                return state;
            }
            case SyntaxKind.ModuleDeclaration:
                return GetModuleInstanceStateCore(node, ancestors, visited);
        }
        return ModuleInstanceState.Instantiated;
    }

    // Go: utilities.go:2387-2422
    private static ModuleInstanceState GetModuleInstanceStateForAliasTarget(Node node, Node[]? ancestors, Dictionary<NodeId, ModuleInstanceState>? visited)
    {
        var name = node.PropertyNameOrName()!;
        if (name.Kind != SyntaxKind.Identifier)
        {
            // Skip for invalid syntax like this: export { "x" }
            return ModuleInstanceState.Instantiated;
        }
        var anc = ancestors;
        Node? p;
        (anc, p) = PopAncestor(anc, node);
        while (p != null)
        {
            if (IsBlock(p) || IsModuleBlock(p) || IsSourceFile(p))
            {
                var found = ModuleInstanceState.Unknown;
                var statementsAncestors = PushAncestor(anc, p);
                foreach (var statement in p.Statements() ?? [])
                {
                    if (NodeHasName(statement, name))
                    {
                        var state = GetModuleInstanceStateCached(statement, statementsAncestors, visited);
                        if (found == ModuleInstanceState.Unknown || state > found)
                        {
                            found = state;
                        }
                        if (found == ModuleInstanceState.Instantiated)
                        {
                            return found;
                        }
                        if (statement.Kind == SyntaxKind.ImportEqualsDeclaration)
                        {
                            // Treat re-exports of import aliases as instantiated since they're ambiguous. This is consistent
                            // with `export import x = mod.x` being treated as instantiated:
                            //   import x = mod.x;
                            //   export { x };
                            found = ModuleInstanceState.Instantiated;
                        }
                    }
                }
                if (found != ModuleInstanceState.Unknown)
                {
                    return found;
                }
            }
            (anc, p) = PopAncestor(anc, p);
        }
        // Couldn't locate, assume could refer to a value
        return ModuleInstanceState.Instantiated;
    }

    // Go: utilities.go:2430-2440
    public static bool NodeHasName(Node statement, Node id)
    {
        var name = statement.Name();
        if (name != null)
        {
            return IsIdentifier(name) && name.Text() == id.Text();
        }
        if (IsVariableStatement(statement))
        {
            var declarations = statement.AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes;
            return CoreUtil.Some(declarations, d => NodeHasName(d, id));
        }
        return false;
    }

    // Go: utilities.go:2446-2452
    public static bool IsConstAssertion(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.AsExpression:
            case SyntaxKind.TypeAssertionExpression:
                return IsConstTypeReference(node.Type()!);
        }
        return false;
    }

    // Go: utilities.go:2454-2456
    public static bool IsConstTypeReference(Node node)
    {
        return IsTypeReferenceNode(node) && (node.TypeArguments()?.Length ?? 0) == 0 && IsIdentifier(node.AsTypeReferenceNode().TypeName!) && node.AsTypeReferenceNode().TypeName!.Text() == "const";
    }

    // Go: utilities.go:2458-2460
    public static bool IsGlobalSourceFile(Node node)
    {
        return node.Kind == SyntaxKind.SourceFile && !IsExternalOrCommonJSModule(node.AsSourceFile());
    }

    // Go: utilities.go:2470-2477
    public static Node? GetDeclarationOfKind(Symbol symbol, SyntaxKind kind)
    {
        foreach (var declaration in symbol.Declarations ?? [])
        {
            if (declaration.Kind == kind)
            {
                return declaration;
            }
        }
        return null;
    }

    // Go: utilities.go:2520-2522
    public static bool ModuleExportNameIsDefault(Node node)
    {
        return node.Text() == InternalSymbolName.Default;
    }

    // Go: utilities.go:2571-2585
    public static Node? GetDeclarationContainer(Node node)
    {
        return FindAncestor(GetRootDeclaration(node), n =>
        {
            switch (n.Kind)
            {
                case SyntaxKind.VariableDeclaration:
                case SyntaxKind.VariableDeclarationList:
                case SyntaxKind.ImportSpecifier:
                case SyntaxKind.NamedImports:
                case SyntaxKind.NamespaceImport:
                case SyntaxKind.ImportClause:
                    return false;
                default:
                    return true;
            }
        })!.Parent;
    }

    // Indicates that a symbol is an alias that does not merge with a local declaration.
    // OR Is a JSContainer which may merge an alias with a local declaration
    // Go: utilities.go:2589-2595
    public static bool IsNonLocalAlias(Symbol? symbol, SymbolFlags excludes)
    {
        if (symbol == null)
        {
            return false;
        }
        return (symbol.Flags & (SymbolFlags.Alias | excludes)) == SymbolFlags.Alias ||
            (symbol.Flags & SymbolFlags.Alias) != 0 && (symbol.Flags & SymbolFlags.Assignment) != 0;
    }

    // An alias symbol is created by one of the following declarations:
    //
    //	import <symbol> = ...
    //	const <symbol> = ... (JS only)
    //	const { <symbol>, ... } = ... (JS only)
    //	import <symbol> from ...
    //	import * as <symbol> from ...
    //	import { x as <symbol> } from ...
    //	export { x as <symbol> } from ...
    //	export * as ns <symbol> from ...
    //	export = <EntityNameExpression>
    //	export default <EntityNameExpression>
    //	module.exports = <EntityNameExpression> (JS only)
    //	module.exports.<symbol> = <EntityNameExpression> (JS only)
    //	exports.<symbol> = <EntityNameExpression> (JS only)
    // Go: utilities.go:2612-2630
    public static bool IsAliasSymbolDeclaration(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.NamespaceExportDeclaration:
            case SyntaxKind.NamespaceImport:
            case SyntaxKind.NamespaceExport:
            case SyntaxKind.ImportSpecifier:
            case SyntaxKind.ExportSpecifier:
                return true;
            case SyntaxKind.ImportClause:
                return node.AsImportClause().Name() != null;
            case SyntaxKind.ExportAssignment:
                return ExpressionIsAlias(node.Expression()!);
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.BindingElement:
                return IsVariableDeclarationInitializedToRequire(node);
            case SyntaxKind.BinaryExpression:
                switch (GetAssignmentDeclarationKind(node))
                {
                    case JSDeclarationKind.ModuleExports:
                    case JSDeclarationKind.ExportsProperty:
                        return ExpressionIsAlias(node.AsBinaryExpression().Right!);
                }
                break;
        }
        return false;
    }

    // Go: utilities.go:2850-2857
    public static bool IsModuleExportsAccessExpression(Node node)
    {
        if (IsAccessExpression(node) && IsModuleIdentifier(node.Expression()!))
        {
            var name = GetElementOrPropertyAccessName(node);
            if (name != null)
            {
                return name.Text() == "exports";
            }
        }
        return false;
    }

    // Returns true if the node is a CallExpression to the identifier 'require' with
    // exactly one argument (of the form 'require("name")').
    // This function does not test if the node is in a JavaScript file or not.
    // Go: utilities.go:2729-2741
    public static bool IsRequireCall(Node node, bool requireStringLiteralLikeArgument)
    {
        if (!IsCallExpression(node))
        {
            return false;
        }
        var call = node.AsCallExpression();
        if (!IsIdentifier(call.Expression!) || call.Expression!.Text() != "require")
        {
            return false;
        }
        if (call.Arguments!.Nodes.Length != 1)
        {
            return false;
        }
        return !requireStringLiteralLikeArgument || IsStringLiteralLike(call.Arguments!.Nodes[0]);
    }

    // Of the form: `const x = require("x")` or `const { x } = require("x")` or with `var` or `let`
    // The variable must not be exported and must not have a type annotation, even a jsdoc one.
    // The initializer must be a call to `require` with a string literal or a string literal-like argument.
    // Go: utilities.go:2806-2811
    public static bool IsVariableDeclarationInitializedToRequire(Node node)
    {
        if (node.Kind == SyntaxKind.BindingElement)
        {
            node = node.Parent!.Parent!;
        }
        return IsVariableDeclarationInitializedWithRequireHelper(node, false /*allowAccessedRequire*/);
    }

    // Go: utilities.go:2817-2835
    private static bool IsVariableDeclarationInitializedWithRequireHelper(Node node, bool allowAccessedRequire)
    {
        if (!IsInJSFile(node))
        {
            return false;
        }
        if (node.Kind != SyntaxKind.VariableDeclaration)
        {
            return false;
        }
        var initializer = node.Initializer();
        if (initializer == null)
        {
            return false;
        }
        if (allowAccessedRequire)
        {
            initializer = GetLeftmostAccessExpression(initializer);
        }

        return (node.Parent!.Parent!.ModifierFlags() & ModifierFlags.Export) == 0 &&
            node.Type() == null &&
            IsRequireCall(initializer, true /*requireStringLiteralLikeArgument*/);
    }

    // Go: utilities.go:2874-2879
    public static Node GetLeftmostAccessExpression(Node expr)
    {
        while (IsAccessExpression(expr))
        {
            expr = expr.Expression()!;
        }
        return expr;
    }

    // Go: utilities.go:4117-4128
    public static bool IsExpandoInitializer(Node declaration, Node? initializer)
    {
        if (initializer == null)
        {
            return false;
        }
        if (IsFunctionExpressionOrArrowFunction(initializer))
        {
            return true;
        }
        if (IsInJSFile(initializer))
        {
            return IsClassExpression(initializer) || (IsObjectLiteralExpression(initializer) && (initializer.Properties()?.Length ?? 0) == 0 && declaration.Type() == null);
        }
        return false;
    }

    // Go: utilities.go:4165-4175
    public static bool IsImplicitlyExportedJSDocDeclaration(Node node)
    {
        if (!IsSourceFile(node.Parent!) || !IsExternalOrCommonJSModule(node.Parent!.AsSourceFile()))
        {
            return false;
        }
        if (IsJSTypeAliasDeclaration(node))
        {
            return true;
        }
        // A reparsed ModuleDeclaration synthesized from a JSDoc @typedef/@callback
        // dotted name should also be treated as implicitly exported in modules.
        return IsModuleDeclaration(node) && (node.Flags & NodeFlags.Reparsed) != 0;
    }

    // Returns true for nodes that are considered executable for the purposes of unreachable code detection.
    // Go: utilities.go:4210-4225
    public static bool IsPotentiallyExecutableNode(Node node)
    {
        if (SyntaxKind.FirstStatement <= node.Kind && node.Kind <= SyntaxKind.LastStatement)
        {
            if (IsVariableStatement(node))
            {
                var declarationList = node.AsVariableStatement().DeclarationList;
                if ((GetCombinedNodeFlags(declarationList!) & NodeFlags.BlockScoped) != 0)
                {
                    return true;
                }
                var declarations = declarationList!.AsVariableDeclarationList().Declarations!.Nodes;
                return CoreUtil.Some(declarations, d => d.Initializer() != null);
            }
            return true;
        }
        return IsClassDeclaration(node) || IsEnumDeclaration(node) || IsModuleDeclaration(node);
    }

    // Go: utilities.go:4415-4422
    public static bool IsAsyncFunction(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.MethodDeclaration:
            {
                var data = node.BodyData()!;
                return data.Body != null && data.AsteriskToken == null && HasSyntacticModifier(node, ModifierFlags.Async);
            }
        }
        return false;
    }
}

// Go: internal/ast/utilities.go:1500-1520 — `type JSDeclarationKind int` iota block. Members
// drop the Go `JSDeclarationKind` type-name prefix (plan/05 §1.1 enum rule).
public enum JSDeclarationKind
{
    None,
    // module.exports = expr, except for module.exports = exports
    ModuleExports,
    // exports.name = expr
    // module.exports.name = expr
    ExportsProperty,
    // this.name = expr
    ThisProperty,
    // F.name = expr, F[name] = expr, in JS or TS file
    Property,
    // Object.defineProperty(x, 'name', { value: any, writable?: boolean (false by default) });
    // Object.defineProperty(x, 'name', { get: Function, set: Function });
    // Object.defineProperty(x, 'name', { get: Function });
    // Object.defineProperty(x, 'name', { set: Function });
    ObjectDefinePropertyValue,
    // Object.defineProperty(exports || module.exports, 'name', ...);
    ObjectDefinePropertyExports,
}

// Go: internal/ast/utilities.go:2294-2301 — `type ModuleInstanceState int32` iota block.
public enum ModuleInstanceState
{
    Unknown,
    NonInstantiated,
    Instantiated,
    ConstEnumOnly,
}

// Go: internal/ast/ast.go:1578-1595 — (*Node).EagerJSDoc. Returns already-parsed/cached JSDoc
// without triggering lazy JSDoc parsing (unlike NodeJSDocAccessors.JSDoc). Landed here (as an
// extension so the generated Node partial is untouched) because scanner.GetErrorRangeForNode's
// findOriginatingJSDocSatisfiesTag is the first consumer. PORT-NOTE: the C# SourceFile has no
// jsdocMu; jsdocCache is read directly, matching NodeJSDocAccessors.JSDoc.
public static class NodeEagerJSDocAccessors
{
    // Go: ast.go:1578-1595
    public static Node[]? EagerJSDoc(this Node node, SourceFile? file)
    {
        if ((node.Flags & NodeFlags.HasJSDoc) == 0)
        {
            return null;
        }
        if (file == null)
        {
            file = AstUtilities.GetSourceFileOfNode(node);
            if (file == null)
            {
                return null;
            }
        }
        return file.jsdocCache?.GetValueOrDefault(node);
    }
}
