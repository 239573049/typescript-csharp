// Ported from tsgo internal/checker/emitresolver.go — type-node synthesis half (.d.ts emit) +
// decorator-metadata serialization kind.
//
// COVERAGE:
//   * GetTypeReferenceSerializationKind (decorator metadata) — FULLY PORTED; uses only existing
//     checker helpers plus GetGlobalPromiseConstructorSymbol.
//   * CreateTypeOfDeclaration / CreateReturnTypeOfSignatureDeclaration /
//     CreateTypeParametersOfSignatureDeclaration / CreateTypeOfExpression / CreateLiteralConstValue /
//     CreateLateBoundIndexSignatures / TryJSTypeNodeToTypeNode — these delegate to the checker's
//     request-scoped NodeBuilder (Go: NewNodeBuilder(checker, emitContext).SerializeX / TypeToTypeNode /
//     SymbolToExpression / IndexInfoToIndexSignatureDeclaration). Those NodeBuilder request-builder
//     entry points are NOT yet ported (NodeBuilder wave landed only TypeToTypeNode's intrinsic core).
//     Until then, `checker.NewNodeBuilder(...)` is a throwing stub (Checker.EmitResolver.Deps.cs);
//     these method bodies faithfully mirror the Go control flow so they light up when the request
//     builder lands. Signatures match the Ts.Printer.EmitResolver interface exactly.
//
// §1.5: EmitContext + TypeReferenceSerializationKind live in `Ts.Printer`; NodeBuilder Flags/
// InternalFlags/ISymbolTracker in `Ts.NodeBuilder`; both referenced via `global::`.

using Ts.Ast;
using Ts.Core;
using Ts.JsNum;

namespace Ts.Checker;

public sealed partial class EmitResolver
{
    // emitresolver.go:942-952
    public Node? CreateReturnTypeOfSignatureDeclaration(
        global::Ts.Printer.EmitContext emitContext, Node signatureDeclaration, Node enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        var original = emitContext.ParseNode(signatureDeclaration);
        if (original is null)
        {
            return emitContext.Factory.AsNodeFactory().NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }

        lock (checkerMu)
        {
            var requestNodeBuilder = checker.NewNodeBuilder(emitContext); // TODO: cache per-context
            return requestNodeBuilder.SerializeReturnTypeForSignature(
                original, enclosingDeclaration, flags, internalFlags, tracker);
        }
    }

    // emitresolver.go:954-964
    public Node[] CreateTypeParametersOfSignatureDeclaration(
        global::Ts.Printer.EmitContext emitContext, Node signatureDeclaration, Node enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        var original = emitContext.ParseNode(signatureDeclaration);
        if (original is null)
        {
            return [];
        }

        lock (checkerMu)
        {
            var requestNodeBuilder = checker.NewNodeBuilder(emitContext); // TODO: cache per-context
            return requestNodeBuilder.SerializeTypeParametersForSignature(
                original, enclosingDeclaration, flags, internalFlags, tracker);
        }
    }

    // emitresolver.go:966-978
    public Node? CreateTypeOfDeclaration(
        global::Ts.Printer.EmitContext emitContext, Node declaration, Node enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        var original = emitContext.ParseNode(declaration);
        if (original is null)
        {
            return emitContext.Factory.AsNodeFactory().NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }

        lock (checkerMu)
        {
            var requestNodeBuilder = checker.NewNodeBuilder(emitContext); // TODO: cache per-context
            // Get type of the symbol if this is the valid symbol otherwise get type at location
            var symbol = checker.GetSymbolOfDeclaration(declaration);
            return requestNodeBuilder.SerializeTypeForDeclaration(
                declaration, symbol, enclosingDeclaration,
                flags | global::Ts.NodeBuilder.Flags.MultilineObjectLiterals, internalFlags, tracker);
        }
    }

    // emitresolver.go:980-1039
    public Node? CreateLiteralConstValue(
        global::Ts.Printer.EmitContext emitContext, Node node, global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        node = emitContext.ParseNode(node)!;
        Type? t;
        lock (checkerMu)
        {
            t = checker.GetTypeOfSymbol(checker.GetSymbolOfDeclaration(node)!);
        }
        if (t is null)
        {
            return null; // TODO: How!? Maybe this should be a panic. All symbols should have a type.
        }

        Node? enumResult = null;
        if ((t.flags & TypeFlags.EnumLike) != 0)
        {
            lock (checkerMu)
            {
                var requestNodeBuilder = checker.NewNodeBuilder(emitContext); // TODO: cache per-context
                enumResult = requestNodeBuilder.SymbolToExpression(
                    t.Symbol!, SymbolFlags.Value, node,
                    global::Ts.NodeBuilder.Flags.None, global::Ts.NodeBuilder.InternalFlags.None, tracker);
                // What about regularTrueType/regularFalseType - since those aren't fresh, we never
                // make initializers from them.
                // TODO: handle those if this function is ever used for more than initializers in
                // declaration emit
            }
        }
        else if (t == checker.trueType)
        {
            enumResult = emitContext.Factory.NewKeywordExpression(SyntaxKind.TrueKeyword);
        }
        else if (t == checker.falseType)
        {
            enumResult = emitContext.Factory.NewKeywordExpression(SyntaxKind.FalseKeyword);
        }
        if (enumResult is not null)
        {
            return enumResult;
        }
        if ((t.flags & TypeFlags.Literal) == 0)
        {
            return null; // non-literal type
        }
        switch (t.AsLiteralType().value)
        {
            case string value:
                return emitContext.Factory.NewStringLiteral(value, TokenFlags.None);
            case Number value:
                if (value.IsInf())
                {
                    if (value > new Number(0))
                    {
                        return emitContext.Factory.NewIdentifier("Infinity");
                    }
                    return emitContext.Factory.AsNodeFactory().NewPrefixUnaryExpression(
                        SyntaxKind.MinusToken, emitContext.Factory.NewIdentifier("Infinity"));
                }
                if (value.IsNaN())
                {
                    return emitContext.Factory.NewIdentifier("NaN");
                }
                if (value.Abs() != value)
                {
                    // negative
                    return emitContext.Factory.AsNodeFactory().NewPrefixUnaryExpression(
                        SyntaxKind.MinusToken,
                        emitContext.Factory.NewNumericLiteral(value.ToString()[1..], TokenFlags.None));
                }
                return emitContext.Factory.NewNumericLiteral(value.ToString(), TokenFlags.None);
            case PseudoBigInt value:
                return emitContext.Factory.AsNodeFactory().NewBigIntLiteral(value + "n", TokenFlags.None);
            case bool value:
            {
                var kind = value ? SyntaxKind.TrueKeyword : SyntaxKind.FalseKeyword;
                return emitContext.Factory.NewKeywordExpression(kind);
            }
        }
        throw new global::System.InvalidOperationException("unhandled literal const value kind");
    }

    // emitresolver.go:1041-1051
    public Node? CreateTypeOfExpression(
        global::Ts.Printer.EmitContext emitContext, Node expression, Node enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        expression = emitContext.ParseNode(expression)!;
        if (expression is null)
        {
            return emitContext.Factory.AsNodeFactory().NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }

        lock (checkerMu)
        {
            var requestNodeBuilder = checker.NewNodeBuilder(emitContext); // TODO: cache per-context
            return requestNodeBuilder.SerializeTypeForExpression(
                expression, enclosingDeclaration,
                flags | global::Ts.NodeBuilder.Flags.MultilineObjectLiterals, internalFlags, tracker);
        }
    }

    // emitresolver.go:1053-1141
    public Node[] CreateLateBoundIndexSignatures(
        global::Ts.Printer.EmitContext emitContext, Node container, Node enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        container = emitContext.ParseNode(container)!;
        lock (checkerMu)
        {
            var sym = container.Symbol()!;
            var staticInfos = checker.GetIndexInfosOfType(checker.GetTypeOfSymbol(sym));
            var instanceIndexSymbol = checker.GetIndexSymbol(sym);
            IndexInfo[]? instanceInfos = null;
            if (instanceIndexSymbol is not null)
            {
                // map-iter: 序无关 — sibling symbols feed getIndexInfosOfIndexSymbol which does not
                // depend on ordering (it filters/collects per-symbol).
                var siblingSymbols = System.Linq.Enumerable.ToArray(checker.GetMembersOfSymbol(sym).Values);
                instanceInfos = checker.GetIndexInfosOfIndexSymbol(instanceIndexSymbol, siblingSymbols);
            }

            var requestNodeBuilder = checker.NewNodeBuilder(emitContext); // TODO: cache per-context

            var result = new System.Collections.Generic.List<Node>();
            var infoLists = new[] { staticInfos, instanceInfos };
            for (var i = 0; i < infoLists.Length; i++)
            {
                var infoList = infoLists[i];
                var isStatic = i <= 0;
                if (infoList is null || infoList.Length == 0)
                {
                    continue;
                }
                foreach (var info in infoList)
                {
                    if (info.declaration is not null)
                    {
                        continue;
                    }
                    if (info == checker.anyBaseTypeIndexInfo)
                    {
                        continue; // inherited, but looks like a late-bound signature because it has no declarations
                    }
                    if (info.components is not null && info.components.Length != 0)
                    {
                        // !!! TODO: Complete late-bound index info support - getObjectLiteralIndexInfo
                        // does not yet add late bound components to index signatures
                        var allComponentComputedNamesSerializable = enclosingDeclaration is not null &&
                            CoreUtil.Every(info.components, c =>
                                c.Name() is not null &&
                                NodeTests.IsComputedPropertyName(c.Name()!) &&
                                AstUtilities.IsEntityNameExpression(c.Name()!.Expression()!) &&
                                IsEntityNameVisibleWorker(c.Name()!.Expression()!, enclosingDeclaration!, false).Accessibility ==
                                    global::Ts.Printer.SymbolAccessibility.Accessible);
                        if (allComponentComputedNamesSerializable)
                        {
                            foreach (var c in info.components)
                            {
                                if (checker.HasLateBindableName(c))
                                {
                                    // skip late bound props that contribute to the index signature -
                                    // they'll be preserved via other means
                                    continue;
                                }

                                var firstIdentifier = Checker.GetFirstIdentifier(c.Name()!.Expression()!);
                                var name = checker.ResolveName(firstIdentifier, firstIdentifier.Text(),
                                    SymbolFlags.Value | SymbolFlags.ExportValue,
                                    null /*nameNotFoundMessage*/, true /*isUse*/, false /*excludeGlobals*/);
                                if (name is not null)
                                {
                                    tracker.TrackSymbol(name, enclosingDeclaration, SymbolFlags.Value);
                                }

                                var mods = isStatic
                                    ? new[] { emitContext.Factory.AsNodeFactory().NewModifier(SyntaxKind.StaticKeyword) }
                                    : null;
                                if (info.isReadonly)
                                {
                                    mods = mods is null
                                        ? [emitContext.Factory.AsNodeFactory().NewModifier(SyntaxKind.ReadonlyKeyword)]
                                        : [.. mods, emitContext.Factory.AsNodeFactory().NewModifier(SyntaxKind.ReadonlyKeyword)];
                                }

                                var decl = emitContext.Factory.AsNodeFactory().NewPropertyDeclaration(
                                    mods is not null ? emitContext.Factory.AsNodeFactory().NewModifierList(mods) : null,
                                    c.Name(),
                                    c.QuestionToken(),
                                    requestNodeBuilder.TypeToTypeNode(
                                        checker.GetTypeOfSymbol(c.Symbol()!), enclosingDeclaration, flags, internalFlags, tracker),
                                    null);
                                result.Add(decl);
                            }
                            continue;
                        }
                    }
                    var node = requestNodeBuilder.IndexInfoToIndexSignatureDeclaration(
                        info, enclosingDeclaration, flags, internalFlags, tracker);
                    if (node is not null && isStatic)
                    {
                        var modNodes = new System.Collections.Generic.List<Node>
                        {
                            emitContext.Factory.AsNodeFactory().NewModifier(SyntaxKind.StaticKeyword),
                        };
                        modNodes.AddRange(node.ModifierNodes() ?? []);
                        var mods = emitContext.Factory.AsNodeFactory().NewModifierList(modNodes.ToArray());
                        node = emitContext.Factory.AsNodeFactory().UpdateIndexSignatureDeclaration(
                            node.AsIndexSignatureDeclaration(),
                            mods,
                            node.ParameterList(),
                            node.Type());
                    }
                    if (node is not null)
                    {
                        result.Add(node);
                    }
                }
            }
            return result.ToArray();
        }
    }

    // emitresolver.go:1164-1254 — decorator metadata serialization kind. FULLY PORTED.
    public global::Ts.Printer.TypeReferenceSerializationKind GetTypeReferenceSerializationKind(
        EntityName name, Node serialScope)
    {
        var typeName = name;
        var location = serialScope;
        // typeName = emitContext.ParseNode(typeName)
        // location = emitContext.ParseNode(location)
        lock (checkerMu)
        {
            if (typeName is null || location is null)
            {
                return global::Ts.Printer.TypeReferenceSerializationKind.Unknown;
            }

            // Resolve the symbol as a value to ensure the type can be reached at runtime during emit.
            var isTypeOnly = false;
            if (NodeTests.IsQualifiedName(typeName))
            {
                var rootValueSymbol = checker.ResolveEntityName(
                    Checker.GetFirstIdentifier(typeName), SymbolFlags.Value, true, true, location);

                if (rootValueSymbol is not null && (rootValueSymbol.Declarations?.Length ?? 0) > 0)
                {
                    isTypeOnly = CoreUtil.Every(rootValueSymbol.Declarations!, d => Checker.IsTypeOnlyImportOrExportDeclaration(d));
                }
            }
            var valueSymbol = checker.ResolveEntityName(typeName, SymbolFlags.Value, true, true, location);
            var resolvedValueSymbol = valueSymbol;
            if (valueSymbol is not null && (valueSymbol.Flags & SymbolFlags.Alias) != 0)
            {
                resolvedValueSymbol = checker.ResolveAlias(valueSymbol).Item1;
            }

            isTypeOnly = isTypeOnly ||
                (valueSymbol is not null && checker.GetTypeOnlyAliasDeclarationEx(valueSymbol, SymbolFlags.Value) is not null);

            // Resolve the symbol as a type so that we can provide a more useful hint for the type serializer.
            var typeSymbol = checker.ResolveEntityName(typeName, SymbolFlags.Type, true, true, location);
            var resolvedTypeSymbol = typeSymbol;
            if (typeSymbol is not null && (typeSymbol.Flags & SymbolFlags.Alias) != 0)
            {
                resolvedTypeSymbol = checker.ResolveAlias(typeSymbol).Item1;
            }
            // In case the value symbol can't be resolved (e.g. because of missing declarations), use
            // type symbol for reachability check.
            isTypeOnly = isTypeOnly ||
                (typeSymbol is not null && checker.GetTypeOnlyAliasDeclarationEx(typeSymbol, SymbolFlags.Type) is not null);

            if (resolvedValueSymbol is not null && resolvedValueSymbol == resolvedTypeSymbol)
            {
                var globalPromiseSymbol = checker.GetGlobalPromiseConstructorSymbol();
                if (globalPromiseSymbol is not null && resolvedValueSymbol == globalPromiseSymbol)
                {
                    return global::Ts.Printer.TypeReferenceSerializationKind.Promise;
                }

                var constructorType = checker.GetTypeOfSymbol(resolvedValueSymbol);
                if (constructorType is not null && checker.IsConstructorType(constructorType))
                {
                    if (isTypeOnly)
                    {
                        return global::Ts.Printer.TypeReferenceSerializationKind.TypeWithCallSignature;
                    }
                    return global::Ts.Printer.TypeReferenceSerializationKind.TypeWithConstructSignatureAndValue;
                }
            }

            // We might not be able to resolve type symbol so use unknown type in that case (eg error case)
            if (resolvedTypeSymbol is null)
            {
                if (isTypeOnly)
                {
                    return global::Ts.Printer.TypeReferenceSerializationKind.ObjectType;
                }
                return global::Ts.Printer.TypeReferenceSerializationKind.Unknown;
            }

            var type = checker.GetDeclaredTypeOfSymbol(resolvedTypeSymbol);
            if (checker.IsErrorType(type))
            {
                if (isTypeOnly)
                {
                    return global::Ts.Printer.TypeReferenceSerializationKind.ObjectType;
                }
                return global::Ts.Printer.TypeReferenceSerializationKind.Unknown;
            }

            if ((type.flags & TypeFlags.AnyOrUnknown) != 0)
            {
                return global::Ts.Printer.TypeReferenceSerializationKind.ObjectType;
            }
            else if (checker.IsTypeAssignableToKind(type, TypeFlags.Void | TypeFlags.Nullable | TypeFlags.Never))
            {
                return global::Ts.Printer.TypeReferenceSerializationKind.VoidNullableOrNeverType;
            }
            else if (checker.IsTypeAssignableToKind(type, TypeFlags.BooleanLike))
            {
                return global::Ts.Printer.TypeReferenceSerializationKind.BooleanType;
            }
            else if (checker.IsTypeAssignableToKind(type, TypeFlags.NumberLike))
            {
                return global::Ts.Printer.TypeReferenceSerializationKind.NumberLikeType;
            }
            else if (checker.IsTypeAssignableToKind(type, TypeFlags.BigIntLike))
            {
                return global::Ts.Printer.TypeReferenceSerializationKind.BigIntLikeType;
            }
            else if (checker.IsTypeAssignableToKind(type, TypeFlags.StringLike))
            {
                return global::Ts.Printer.TypeReferenceSerializationKind.StringLikeType;
            }
            else if (Checker.IsTupleType(type))
            {
                return global::Ts.Printer.TypeReferenceSerializationKind.ArrayLikeType;
            }
            else if (checker.IsTypeAssignableToKind(type, TypeFlags.ESSymbolLike))
            {
                return global::Ts.Printer.TypeReferenceSerializationKind.ESSymbolType;
            }
            else if (checker.IsFunctionType(type))
            {
                return global::Ts.Printer.TypeReferenceSerializationKind.TypeWithCallSignature;
            }
            else if (checker.IsArrayType(type))
            {
                return global::Ts.Printer.TypeReferenceSerializationKind.ArrayLikeType;
            }
            else
            {
                return global::Ts.Printer.TypeReferenceSerializationKind.ObjectType;
            }
        }
    }

    // emitresolver.go:1271-1278
    public Node? TryJSTypeNodeToTypeNode(
        global::Ts.Printer.EmitContext emitContext, Node typeNode, Node enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        typeNode = emitContext.ParseNode(typeNode)!;
        lock (checkerMu)
        {
            var requestNodeBuilder = checker.NewNodeBuilder(emitContext); // TODO: cache per-context
            return requestNodeBuilder.TryJSTypeNodeToTypeNode(
                typeNode, enclosingDeclaration, flags, internalFlags, tracker);
        }
    }
}
