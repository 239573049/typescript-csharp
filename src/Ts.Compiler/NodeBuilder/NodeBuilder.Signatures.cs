// Ported from tsgo internal/checker/nodebuilderscopes.go (whole file) and the signature-declaration
// synthesis slice of internal/checker/nodebuilderimpl.go — the path that turns a Signature into a
// synthesized signature declaration node (`<T>(a: T) => void`, `{ f(): void; }`, call/construct
// signatures, accessors). This backs the error-message serializer for function-typed sources and the
// .d.ts declaration emitter.
//
// PLACEMENT (§1.5): NodeBuilderImpl lives in `package checker` in tsgo, so its faithful C# home is
// namespace Ts.Checker; this partial extends the same NodeBuilder class as NodeBuilder/NodeBuilderImpl.cs.
// getExpandedParameters has a Checker receiver in nodebuilderimpl.go, so it lands on the Checker
// partial declared at the bottom of this file (same file mapping as Go).
//
// SCOPE (this wave — signature-declaration synthesis):
//   FULLY PORTED:
//     * cloneNodeBuilderContext / addSymbolTypeToContext / enterSignatureScope / enterNewScope
//       (nodebuilderscopes.go:10/40/53/59 — fake-scope injection included)
//     * signatureToSignatureDeclarationHelper (nodebuilderimpl.go:1786)
//     * typeParameterToDeclarationWithConstraint / typeParameterToDeclaration / typeParameterToName /
//       typeParameterShadowsOtherTypeParameterInScope (nodebuilderimpl.go:1323/1605/1399/1390)
//     * setTextRange (builder variant, nodebuilderimpl.go:1356)
//     * symbolToParameterDeclaration / parameterToParameterDeclarationName /
//       getEffectiveParameterDeclaration (nodebuilderimpl.go:1648/1690/1638)
//     * serializeInferredReturnTypeForSignature / typePredicateToTypePredicateNodeHelper
//       (nodebuilderimpl.go:1739/1759)
//     * tryGetThisParameterDeclaration / serializeReturnTypeForSignature (nodebuilderimpl.go:1994/2017)
//     * getExpandedParameters (nodebuilderimpl.go:1906, Checker receiver)
//   DEFERRED (documented fall-through / NotImplementedException; owned by later waves):
//     * the pseudo-checker return-type reuse fast path in serializeReturnTypeForSignature
//       (nodebuilderimpl.go:2040-2058) — falls through to serializeInferredReturnTypeForSignature,
//       matching the in-file precedent of SerializeTypeForDeclarationImpl (reuse disabled ⇒ same output
//       as Go when reuse yields no node).
//     * tryReuseExistingNodeHelper inside typeToTypeNodeHelperWithPossibleReusableTypeNode — likewise
//       falls through to typeToTypeNode.
//     * cloneBindingName (binding-pattern parameter names in synthesized signatures — needs the
//       cloneBindingNameVisitor); identifier/qualified-name parameter names are fully handled.
//
// R05 (深递归): synthesis recurses through TypeToTypeNodeImpl, which is only entered underneath
// checker entry points already wrapped by LargeStackExecutor (AGENTS.md §4 铁律8).

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

using PrinterNs = global::Ts.Printer;

namespace Ts.Checker;

// nodebuilderimpl.go:49-52 — NodeBuilderLinks. PORT-NOTE: the serializedTypes cache half is owned by the
// deferred node-reuse wave; only the fake-scope marker is populated here.
internal sealed class NodeBuilderLinks
{
    internal string? fakeScopeForSignatureDeclaration; // If present, this is a fake scope injected into an enclosing declaration chain.
}

// nodebuilderimpl.go:53-55 — NodeBuilderSymbolLinks. Per-symbol module-specifier cache keyed by
// (enclosing-file path, resolution mode).
internal sealed class NodeBuilderSymbolLinks
{
    internal global::Ts.Module.ModeAwareCache<string>? specifierCache;
}

public sealed partial class NodeBuilder
{
    // nodebuilderimpl.go:99 — links core.LinkStore[*ast.Node, NodeBuilderLinks].
    private readonly LinkStore<Node, NodeBuilderLinks> links = new();

    // nodebuilderimpl.go:100 — symbolLinks core.LinkStore[*ast.Symbol, NodeBuilderSymbolLinks].
    private readonly LinkStore<Symbol, NodeBuilderSymbolLinks> symbolLinks = new();

    // nodebuilderscopes.go:10 — cloneNodeBuilderContext.
    // PORT-NOTE (shape): Go snapshots four CopyOnWrite containers via EnterScope; the C# COW Scope is a
    // ref struct (05 §5.2) that cannot be captured by the cleanup closure enterNewScope returns, so the
    // context holds plain containers and this snapshot-restores them eagerly — identical restore
    // semantics, an eager clone instead of copy-on-write laziness.
    private Action CloneNodeBuilderContext()
    {
        // Make type parameters created within this context not consume the name outside this context
        // The symbol serializer ends up creating many sibling scopes that all need "separate" contexts when
        // it comes to naming things - within a normal `typeToTypeNode` call, the node builder only ever descends
        // through the type tree, so the only cases where we could have used distinct sibling scopes was when there
        // were multiple generic overloads with similar generated type parameter names
        // The effect:
        // When we write out
        // export const x: <T>(x: T) => T
        // export const y: <T>(x: T) => T
        // we write it out like that, rather than as
        // export const x: <T>(x: T) => T
        // export const y: <T_1>(x: T_1) => T_1
        var savedNames = new Dictionary<TypeId, Node>(ctx!.typeParameterNames);
        var savedNamesByText = new HashSet<string>(ctx.typeParameterNamesByText, StringComparer.Ordinal);
        var savedNextNameCount = new Dictionary<string, int>(ctx.typeParameterNamesByTextNextNameCount, StringComparer.Ordinal);
        var savedSymbolList = new HashSet<SymbolId>(ctx.typeParameterSymbolList);
        return () =>
        {
            ctx.typeParameterNames.Clear();
            // map-iter: 序无关 — restoring a snapshot into a lookup table; only membership matters.
            foreach (var (k, v) in savedNames)
            {
                ctx.typeParameterNames[k] = v;
            }
            ctx.typeParameterNamesByText.Clear();
            ctx.typeParameterNamesByText.UnionWith(savedNamesByText);
            ctx.typeParameterNamesByTextNextNameCount.Clear();
            // map-iter: 序无关 — restoring a snapshot into a lookup table; only membership matters.
            foreach (var (k, v) in savedNextNameCount)
            {
                ctx.typeParameterNamesByTextNextNameCount[k] = v;
            }
            ctx.typeParameterSymbolList.Clear();
            ctx.typeParameterSymbolList.UnionWith(savedSymbolList);
        };
    }

    // nodebuilderscopes.go:40 — addSymbolTypeToContext.
    private Action AddSymbolTypeToContext(Symbol symbol, Type t)
    {
        var id = AstUtilities.GetSymbolId(symbol);
        var oldTypeExists = ctx!.enclosingSymbolTypes.TryGetValue(id, out var oldType);
        ctx.enclosingSymbolTypes[id] = t;
        return () =>
        {
            if (oldTypeExists)
            {
                ctx.enclosingSymbolTypes[id] = oldType!;
            }
            else
            {
                ctx.enclosingSymbolTypes.Remove(id);
            }
        };
    }

    // nodebuilderscopes.go:53 — enterSignatureScope.
    private (Symbol[], Action) EnterSignatureScope(Signature signature)
    {
        var expandedParams = ch.GetExpandedParameters(signature, true /*skipUnionExpanding*/)[0];
        var cleanup = EnterNewScope(signature.declaration, expandedParams, signature.typeParameters, signature.parameters, signature.mapper);
        return (expandedParams, cleanup);
    }

    // nodebuilderscopes.go:59 — enterNewScope.
    private Action EnterNewScope(Node? declaration, Symbol[]? expandedParams, Type[]? typeParameters, Symbol[]? originalParameters, TypeMapper? mapper)
    {
        var cleanupContext = CloneNodeBuilderContext();
        // For regular function/method declarations, the enclosing declaration will already be signature.declaration,
        // so this is a no-op, but for arrow functions and function expressions, the enclosing declaration will be
        // the declaration that the arrow function / function expression is assigned to.
        //
        // If the parameters or return type include "typeof globalThis.paramName", using the wrong scope will lead
        // us to believe that we can emit "typeof paramName" instead, even though that would refer to the parameter,
        // not the global. Make sure we are in the right scope by changing the enclosingDeclaration to the function.
        //
        // We can't use the declaration directly; it may be in another file and so we may lose access to symbols
        // accessible to the current enclosing declaration, or gain access to symbols not accessible to the current
        // enclosing declaration. To keep this chain accurate, insert a fake scope into the chain which makes the
        // function's parameters visible.
        Action? cleanupParams = null;
        Action? cleanupTypeParams = null;
        var oldEnclosingDecl = ctx!.enclosingDeclaration;
        var oldMapper = ctx.mapper;
        if (mapper is not null)
        {
            ctx.mapper = mapper;
        }
        if (ctx.enclosingDeclaration is not null && declaration is not null)
        {
            // As a performance optimization, reuse the same fake scope within this chain.
            // This is especially needed when we are working on an excessively deep type;
            // if we don't do this, then we spend all of our time adding more and more
            // scopes that need to be searched in isSymbolAccessible later. Since all we
            // really want to do is to mark certain names as unavailable, we can just keep
            // all of the names we're introducing in one large table and push/pop from it as
            // needed; isSymbolAccessible will walk upward and find the closest "fake" scope,
            // which will conveniently report on any and all faked scopes in the chain.
            //
            // It'd likely be better to store this somewhere else for isSymbolAccessible, but
            // since that API _only_ uses the enclosing declaration (and its parents), this is
            // seems like the best way to inject names into that search process.
            //
            // Note that we only check the most immediate enclosingDeclaration; the only place we
            // could potentially add another fake scope into the chain is right here, so we don't
            // traverse all ancestors.
            Action? PushFakeScope(string kind, Action<Action<string, Symbol>> addAll)
            {
                // We only ever need to look two declarations upward.
                global::Ts.Core.Debug.Assert(ctx.enclosingDeclaration is not null);
                Node? existingFakeScope = null;
                if (links.Has(ctx.enclosingDeclaration!))
                {
                    var scopeLinks = links.Get(ctx.enclosingDeclaration!);
                    if (scopeLinks.fakeScopeForSignatureDeclaration is not null && scopeLinks.fakeScopeForSignatureDeclaration == kind)
                    {
                        existingFakeScope = ctx.enclosingDeclaration;
                    }
                }
                if (existingFakeScope is null && ctx.enclosingDeclaration!.Parent is not null)
                {
                    if (links.Has(ctx.enclosingDeclaration.Parent))
                    {
                        var scopeLinks = links.Get(ctx.enclosingDeclaration.Parent);
                        if (scopeLinks.fakeScopeForSignatureDeclaration is not null && scopeLinks.fakeScopeForSignatureDeclaration == kind)
                        {
                            existingFakeScope = ctx.enclosingDeclaration.Parent;
                        }
                    }
                }
                global::Ts.Core.Debug.Assert(existingFakeScope is null || NodeTests.IsBlock(existingFakeScope));

                SymbolTable? locals = null;
                if (existingFakeScope is not null)
                {
                    locals = existingFakeScope.Locals();
                }
                locals ??= new SymbolTable();
                var newLocals = new List<string>();
                var oldLocals = new List<(string Name, Symbol OldSymbol)>();
                addAll((name, symbol) =>
                {
                    // Add cleanup information only if we don't own the fake scope
                    if (existingFakeScope is not null)
                    {
                        var oldSymbol = locals.GetValueOrDefault(name);
                        if (oldSymbol is null)
                        {
                            newLocals.Add(name);
                        }
                        else
                        {
                            oldLocals.Add((name, oldSymbol));
                        }
                    }
                    locals[name] = symbol;
                });

                if (existingFakeScope is null)
                {
                    // Use a Block for this; the type of the node doesn't matter so long as it
                    // has locals, and this is cheaper/easier than using a function-ish Node.
                    var fakeScope = f.NewBlock(f.NewNodeList([]), false);
                    links.Get(fakeScope).fakeScopeForSignatureDeclaration = kind;
                    var data = fakeScope.LocalsContainerData()!;
                    data.Locals = locals;
                    fakeScope.Parent = ctx.enclosingDeclaration;
                    ctx.enclosingDeclaration = fakeScope;
                    return null;
                }
                else
                {
                    // We did not create the current scope, so we have to clean it up
                    void Undo()
                    {
                        foreach (var s in newLocals)
                        {
                            locals.Remove(s);
                        }
                        foreach (var s in oldLocals)
                        {
                            locals[s.Name] = s.OldSymbol;
                        }
                    }
                    return Undo;
                }
            }

            if (expandedParams is null || !CoreUtil.Some(expandedParams, static p => p is not null))
            {
                cleanupParams = null;
            }
            else
            {
                cleanupParams = PushFakeScope("params", add =>
                {
                    if (expandedParams is null)
                    {
                        return;
                    }
                    for (var pIndex = 0; pIndex < expandedParams.Length; pIndex++)
                    {
                        var param = expandedParams[pIndex];
                        Symbol? originalParam = null;
                        if (originalParameters is not null && pIndex < originalParameters.Length)
                        {
                            originalParam = originalParameters[pIndex];
                        }
                        if (originalParameters is not null && originalParam != param)
                        {
                            // Can't reference the expanded parameter name, just the original, unless we've expanded the param list for some reason
                            if (originalParam is not null)
                            {
                                add(originalParam.Name, originalParam);
                            }
                        }
                        else if (!CoreUtil.Some(param.Declarations, d =>
                        {
                            Action<Node>? bindElement = null;
                            Action<Node>? bindPattern = null;

                            void BindPatternWorker(Node p)
                            {
                                foreach (var e in p.AsBindingPattern().Elements!.Nodes)
                                {
                                    switch (e.Kind)
                                    {
                                        case SyntaxKind.OmittedExpression:
                                            return;
                                        case SyntaxKind.BindingElement:
                                            bindElement!(e);
                                            return;
                                        default:
                                            throw new InvalidOperationException("Unhandled binding element kind");
                                    }
                                }
                            }

                            void BindElementWorker(Node e)
                            {
                                if (e.Name() is not null && AstUtilities.IsBindingPattern(e.Name()))
                                {
                                    bindPattern!(e.Name()!);
                                    return;
                                }
                                var symbol = ch.GetSymbolOfDeclaration(e);
                                if (symbol is not null)
                                {
                                    // omitted expressions are now parsed as nameless binding patterns and also have no symbol
                                    add(symbol.Name, symbol);
                                }
                            }
                            bindElement = BindElementWorker;
                            bindPattern = BindPatternWorker;

                            if (NodeTests.IsParameterDeclaration(d) && d.Name() is not null && AstUtilities.IsBindingPattern(d.Name()))
                            {
                                bindPattern(d.Name()!);
                                return true;
                            }
                            return false;
                        }))
                        {
                            add(param.Name, param);
                        }
                    }
                });
            }

            if ((ctx.flags & global::Ts.NodeBuilder.Flags.GenerateNamesForShadowedTypeParams) != 0 && typeParameters is not null && CoreUtil.Some(typeParameters, static p => p is not null))
            {
                cleanupTypeParams = PushFakeScope("typeParams", add =>
                {
                    if (typeParameters is null)
                    {
                        return;
                    }
                    foreach (var typeParam in typeParameters)
                    {
                        if (typeParam is null)
                        {
                            continue;
                        }
                        var typeParamName = TypeParameterToName(typeParam).Text();
                        add(typeParamName, typeParam.symbol!);
                    }
                });
            }
        }

        return () =>
        {
            cleanupParams?.Invoke();
            cleanupTypeParams?.Invoke();
            cleanupContext();
            ctx.enclosingDeclaration = oldEnclosingDecl;
            ctx.mapper = oldMapper;
        };
    }

    // nodebuilderimpl.go:1786 — signatureToSignatureDeclarationHelper.
    private Node SignatureToSignatureDeclarationHelper(Signature signature, SyntaxKind kind, SignatureToSignatureDeclarationOptions? options)
    {
        List<Node>? typeParameters = null;

        var (expandedParams, cleanup) = EnterSignatureScope(signature);
        ctx!.approximateLength += 3;
        // Usually a signature contributes a few more characters than this, but 3 is the minimum

        if ((ctx.flags & global::Ts.NodeBuilder.Flags.WriteTypeArgumentsOfSignature) != 0 && signature.target is not null && signature.mapper is not null && (signature.target.typeParameters ?? []).Length != 0)
        {
            foreach (var parameter in signature.target.typeParameters!)
            {
                (typeParameters ??= new List<Node>()).Add(TypeToTypeNodeImpl(ch.InstantiateType(parameter, signature.mapper)));
            }
        }
        else
        {
            foreach (var parameter in signature.typeParameters ?? [])
            {
                (typeParameters ??= new List<Node>()).Add(TypeParameterToDeclaration(parameter));
            }
        }

        var restoreFlags = SaveRestoreFlags();
        ctx.flags &= ~global::Ts.NodeBuilder.Flags.SuppressAnyReturnType;
        // If the expanded parameter list had a variadic in a non-trailing position, don't expand it
        var parameters = CoreUtil.Map(CoreUtil.IfElse(CoreUtil.Some(expandedParams, p =>
            p != expandedParams[expandedParams.Length - 1] && (p.CheckFlags & CheckFlags.RestParameter) != 0), signature.parameters, expandedParams),
            parameter => SymbolToParameterDeclaration(parameter, kind == SyntaxKind.Constructor)) ?? [];
        Node? thisParameter;
        if ((ctx.flags & global::Ts.NodeBuilder.Flags.OmitThisParameter) != 0)
        {
            thisParameter = null;
        }
        else
        {
            thisParameter = TryGetThisParameterDeclaration(signature);
        }
        if (thisParameter is not null)
        {
            parameters = [thisParameter, .. parameters];
        }
        restoreFlags();

        var returnTypeNode = SerializeReturnTypeForSignature(signature, true);

        Node[]? modifiers = null;
        if (options is not null)
        {
            modifiers = options.modifiers;
        }
        if (kind == SyntaxKind.ConstructorType && (signature.flags & SignatureFlags.Abstract) != 0)
        {
            var flags = AstUtilities.ModifiersToFlags(modifiers);
            modifiers = AstUtilities.CreateModifiersFromModifierFlags(flags | ModifierFlags.Abstract, f.NewModifier);
        }

        var paramList = f.NewNodeList(parameters);
        NodeList? typeParamList = null;
        if (typeParameters is not null && typeParameters.Count != 0)
        {
            typeParamList = f.NewNodeList(typeParameters.ToArray());
        }
        ModifierList? modifierList = null;
        if (modifiers is not null && modifiers.Length > 0)
        {
            modifierList = f.NewModifierList(modifiers);
        }
        Node? name = null;
        if (options is not null)
        {
            name = options.name;
        }
        name ??= f.NewIdentifier("");

        Node node;
        switch (kind)
        {
            case SyntaxKind.CallSignature:
                node = f.NewCallSignatureDeclaration(typeParamList, paramList, returnTypeNode);
                break;
            case SyntaxKind.ConstructSignature:
                node = f.NewConstructSignatureDeclaration(typeParamList, paramList, returnTypeNode);
                break;
            case SyntaxKind.MethodSignature:
            {
                Node? questionToken = null;
                if (options is not null)
                {
                    questionToken = options.questionToken;
                }
                node = f.NewMethodSignatureDeclaration(modifierList, name, questionToken, typeParamList, paramList, returnTypeNode);
                break;
            }
            case SyntaxKind.MethodDeclaration:
                node = f.NewMethodDeclaration(modifierList, null /*asteriskToken*/, name, null /*questionToken*/, typeParamList, paramList, returnTypeNode, null /*fullSignature*/, null /*body*/);
                break;
            case SyntaxKind.Constructor:
                node = f.NewConstructorDeclaration(modifierList, null /*typeParamList*/, paramList, null /*returnTypeNode*/, null /*fullSignature*/, null /*body*/);
                break;
            case SyntaxKind.GetAccessor:
                node = f.NewGetAccessorDeclaration(modifierList, name, null /*typeParamList*/, paramList, returnTypeNode, null /*fullSignature*/, null /*body*/);
                break;
            case SyntaxKind.SetAccessor:
                node = f.NewSetAccessorDeclaration(modifierList, name, null /*typeParamList*/, paramList, null /*returnTypeNode*/, null /*fullSignature*/, null /*body*/);
                break;
            case SyntaxKind.IndexSignature:
                node = f.NewIndexSignatureDeclaration(modifierList, paramList, returnTypeNode);
                break;
            // !!! JSDoc Support
            // case SyntaxKind.JSDocFunctionType:
            case SyntaxKind.FunctionType:
                returnTypeNode ??= f.NewTypeReferenceNode(f.NewIdentifier(""), null);
                node = f.NewFunctionTypeNode(typeParamList, paramList, returnTypeNode);
                break;
            case SyntaxKind.ConstructorType:
                returnTypeNode ??= f.NewTypeReferenceNode(f.NewIdentifier(""), null);
                node = f.NewConstructorTypeNode(modifierList, typeParamList, paramList, returnTypeNode);
                break;
            case SyntaxKind.FunctionDeclaration:
                // TODO: assert name is Identifier
                node = f.NewFunctionDeclaration(modifierList, null /*asteriskToken*/, name, typeParamList, paramList, returnTypeNode, null /*fullSignature*/, null /*body*/);
                break;
            case SyntaxKind.FunctionExpression:
                // TODO: assert name is Identifier
                node = f.NewFunctionExpression(modifierList, null /*asteriskToken*/, name, typeParamList, paramList, returnTypeNode, null /*fullSignature*/, f.NewBlock(f.NewNodeList([]), false));
                break;
            case SyntaxKind.ArrowFunction:
                node = f.NewArrowFunction(modifierList, typeParamList, paramList, returnTypeNode, null /*fullSignature*/, null /*equalsGreaterThanToken*/, f.NewBlock(f.NewNodeList([]), false));
                break;
            default:
                throw new InvalidOperationException("Unhandled kind in signatureToSignatureDeclarationHelper");
        }

        // !!! TODO: Smuggle type arguments of signatures out for quickinfo
        // if typeArguments != nil {
        // 	node.TypeArguments = b.f.NewNodeList(typeArguments)
        // }

        cleanup();
        return node;
    }

    // nodebuilderimpl.go:1323 — typeParameterToDeclarationWithConstraint.
    private Node TypeParameterToDeclarationWithConstraint(Type typeParameter, Node? constraintNode)
    {
        var restoreFlags = SaveRestoreFlags();
        ctx!.flags &= ~global::Ts.NodeBuilder.Flags.WriteTypeParametersInQualifiedName; // Avoids potential infinite loop when building for a claimspace with a generic
        var modifiers = AstUtilities.CreateModifiersFromModifierFlags(ch.GetTypeParameterModifiers(typeParameter), f.NewModifier);
        ModifierList? modifiersList = null;
        if (modifiers.Length > 0)
        {
            modifiersList = f.NewModifierList(modifiers);
        }
        var name = TypeParameterToName(typeParameter);
        var defaultParameter = ch.GetDefaultFromTypeParameter(typeParameter);
        Node? defaultParameterDeclarationNode = null;
        if (defaultParameter is not null)
        {
            defaultParameterDeclarationNode = TypeToTypeNodeImpl(defaultParameter);
        }
        restoreFlags();
        return f.NewTypeParameterDeclaration(
            modifiersList,
            name,
            constraintNode,
            null, // expression
            defaultParameterDeclarationNode);
    }

    /**
    * Unlike the utilities `setTextRange`, this checks if the `location` we're trying to set on `range` is within the
    * same file as the active context. If not, the range is not applied. This prevents us from copying ranges across files,
    * which will confuse the node printer (as it assumes all node ranges are within the current file).
    * Additionally, if `range` _isn't synthetic_, or isn't in the current file, it will _copy_ it to _remove_ its' position
    * information.
    *
    * It also calls `setOriginalNode` to setup a `.original` pointer, since you basically *always* want these in the node builder.
     */
    // nodebuilderimpl.go:1356 — setTextRange (builder variant). PORT-NOTE: the idToSymbol side-table
    // copy (inlay-hint support) follows the NewIdentifier precedent and is deferred with that wave.
    private Node? SetTextRangeLocal(Node? range, Node? location)
    {
        if (range is null)
        {
            return range;
        }
        if (!AstUtilities.NodeIsSynthesized(range) || (range.Flags & NodeFlags.Synthesized) == 0 || ctx!.enclosingFile is null || ctx.enclosingFile != AstUtilities.GetSourceFileOfNode(e.MostOriginal(range)!))
        {
            range = range.Clone(f)!; // if `range` is synthesized or originates in another file, copy it so it definitely has synthetic positions
            range.Loc = new TextRange(-1, -1);
        }
        if (range == location || location is null)
        {
            return range;
        }
        // Don't overwrite the original node if `range` has an `original` node that points either directly or indirectly to `location`
        var original = e.Original(range);
        while (original is not null && original != location)
        {
            original = e.Original(original);
        }
        if (original is null)
        {
            e.SetOriginalEx(range, location, true);
        }

        // only set positions if range comes from the same file since copying text across files isn't supported by the emitter
        if (ctx!.enclosingFile is not null && ctx.enclosingFile == AstUtilities.GetSourceFileOfNode(e.MostOriginal(location)!))
        {
            range.Loc = location.Loc;
            return range;
        }
        range.Loc = new TextRange(-1, -1);
        return range;
    }

    // nodebuilderimpl.go:1390 — typeParameterShadowsOtherTypeParameterInScope.
    private bool TypeParameterShadowsOtherTypeParameterInScope(string name, Type typeParameter)
    {
        var result = ch.ResolveName(ctx!.enclosingDeclaration, name, SymbolFlags.Type, null, false, false);
        if (result is not null && (result.Flags & SymbolFlags.TypeParameter) != 0)
        {
            return result != typeParameter.symbol;
        }
        return false;
    }

    // nodebuilderimpl.go:1399 — typeParameterToName.
    private Node TypeParameterToName(Type typeParameter)
    {
        if ((ctx!.flags & global::Ts.NodeBuilder.Flags.GenerateNamesForShadowedTypeParams) != 0)
        {
            if (ctx.typeParameterNames.TryGetValue(typeParameter.id, out var cached))
            {
                return cached;
            }
        }
        var result = SymbolToNameImpl(typeParameter.symbol!, SymbolFlags.Type /*expectsIdentifier*/, true);
        if (!NodeTests.IsIdentifier(result))
        {
            return f.NewIdentifier("(Missing type parameter)");
        }
        if (typeParameter.symbol is not null && (typeParameter.symbol.Declarations ?? []).Length > 0)
        {
            var decl = typeParameter.symbol.Declarations![0];
            if (decl is not null && NodeTests.IsTypeParameterDeclaration(decl))
            {
                result = SetTextRangeLocal(result, decl.Name())!;
            }
        }
        if ((ctx.flags & global::Ts.NodeBuilder.Flags.GenerateNamesForShadowedTypeParams) != 0)
        {
            var rawText = result.Text();
            ctx.typeParameterNamesByTextNextNameCount.TryGetValue(rawText, out var i);
            var text = rawText;

            while (true)
            {
                if (!ctx.typeParameterNamesByText.Contains(text) && !TypeParameterShadowsOtherTypeParameterInScope(text, typeParameter))
                {
                    break;
                }
                i++;
                text = rawText + "_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (text != rawText)
            {
                // !!! TODO: smuggle type arguments out
                // const typeArguments = getIdentifierTypeArguments(result);
                result = NewIdentifier(text, typeParameter.symbol);
                // setIdentifierTypeArguments(result, typeArguments);
            }

            // avoiding iterations of the above loop turns out to be worth it when `i` starts to get large, so we cache the max
            // `i` we've used thus far, to save work later
            ctx.typeParameterNamesByTextNextNameCount[rawText] = i;
            ctx.typeParameterNames[typeParameter.id] = result;
            ctx.typeParameterNamesByText.Add(text);
        }

        return result;
    }

    // nodebuilderimpl.go:225 — isActivelyExpanding.
    private bool IsActivelyExpanding() => ctx!.maxExpansionDepth > 0 && ctx.depth < ctx.maxExpansionDepth;

    // nodebuilderimpl.go:2858 — getTypeFromTypeNode (builder variant).
    private Type? GetTypeFromTypeNodeLocal(Node node, bool noMappedTypes)
    {
        // !!! noMappedTypes optional param support
        var t = ch.GetTypeFromTypeNode(node);
        if (ctx!.mapper is null)
        {
            return t;
        }

        var instantiated = ch.InstantiateType(t, ctx.mapper);
        if (noMappedTypes && instantiated != t)
        {
            return null;
        }
        return instantiated;
    }

    // nodebuilderimpl.go:1591 — typeToTypeNodeHelperWithPossibleReusableTypeNode.
    // PORT-NOTE: isActivelyExpanding()/checkTypeExpandability are hover-expansion-only (maxExpansionDepth
    // is fixed at -1 for the error-message / .d.ts callers), so they are omitted; the reuse guard is the
    // faithful common path. If tryReuseExistingNodeHelper bails (finalizeBoundary=false) it returns null
    // and we fall through to typeToTypeNode — identical to the pre-reuse behavior, hence zero-regression.
    private Node TypeToTypeNodeHelperWithPossibleReusableTypeNode(Type? t, Node? typeNode)
    {
        if (t is null)
        {
            return f.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }
        if (typeNode != null && ch.GetTypeFromTypeNode(typeNode) == t)
        {
            var reused = TryReuseExistingNodeHelper(typeNode);
            if (reused != null)
            {
                return reused;
            }
        }
        return TypeToTypeNodeImpl(t);
    }

    // nodebuilderimpl.go:1605 — typeParameterToDeclaration.
    private Node TypeParameterToDeclaration(Type parameter)
    {
        var constraint = ch.GetConstraintOfTypeParameter(parameter);
        Node? constraintNode = null;
        if (constraint is not null)
        {
            constraintNode = TypeToTypeNodeHelperWithPossibleReusableTypeNode(constraint, ch.GetConstraintDeclaration(parameter));
        }
        return TypeParameterToDeclarationWithConstraint(parameter, constraintNode);
    }

    // nodebuilderimpl.go:1638 — getEffectiveParameterDeclaration (free func).
    private static Node? GetEffectiveParameterDeclaration(Symbol symbol)
    {
        var parameterDeclaration = AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.Parameter);
        if (parameterDeclaration is not null)
        {
            return parameterDeclaration;
        }
        if ((symbol.Flags & SymbolFlags.Transient) == 0)
        {
            return AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.JSDocParameterTag);
        }
        return null;
    }

    // nodebuilderimpl.go:1648 — symbolToParameterDeclaration.
    private Node SymbolToParameterDeclaration(Symbol parameterSymbol, bool preserveModifierFlags)
    {
        var parameterDeclaration = GetEffectiveParameterDeclaration(parameterSymbol);

        var parameterType = ch.GetTypeOfSymbol(parameterSymbol);
        var parameterTypeNode = SerializeTypeForDeclarationImpl(parameterDeclaration, parameterType, parameterSymbol, true);
        ModifierList? modifiers = null;
        if ((ctx!.flags & global::Ts.NodeBuilder.Flags.OmitParameterModifiers) == 0 && preserveModifierFlags && parameterDeclaration is not null && Checker.CanHaveModifiers(parameterDeclaration))
        {
            var originals = CoreUtil.Filter(parameterDeclaration.ModifierNodes(), AstUtilities.IsModifier);
            var clones = CoreUtil.Map(originals, node => node.Clone(f));
            if ((clones ?? []).Length > 0)
            {
                modifiers = f.NewModifierList(clones!);
            }
        }
        var isRest = parameterDeclaration is not null && Checker.IsRestParameter(parameterDeclaration) || (parameterSymbol.CheckFlags & CheckFlags.RestParameter) != 0;
        Node? dotDotDotToken = null;
        if (isRest)
        {
            dotDotDotToken = f.NewToken(SyntaxKind.DotDotDotToken);
        }
        var name = ParameterToParameterDeclarationName(parameterSymbol, parameterDeclaration);
        var isOptional = parameterDeclaration is not null && ch.IsOptionalParameter(parameterDeclaration) || (parameterSymbol.CheckFlags & CheckFlags.OptionalParameter) != 0;
        Node? questionToken = null;
        if (isOptional)
        {
            questionToken = f.NewToken(SyntaxKind.QuestionToken);
        }

        var parameterNode = f.NewParameterDeclaration(
            modifiers,
            dotDotDotToken,
            name,
            questionToken,
            parameterTypeNode,
            /*initializer*/ null);
        ctx.approximateLength += parameterSymbol.Name.Length + 3;
        return parameterNode;
    }

    // nodebuilderimpl.go:1690 — parameterToParameterDeclarationName. PORT-NOTE: the idToSymbol
    // side-table (inlay-hint support) follows the NewIdentifier precedent and is deferred.
    private Node ParameterToParameterDeclarationName(Symbol parameterSymbol, Node? parameterDeclaration)
    {
        if (parameterDeclaration is null || parameterDeclaration.Name() is null)
        {
            return NewIdentifier(parameterSymbol.Name, parameterSymbol);
        }

        var name = parameterDeclaration.Name()!;
        switch (name.Kind)
        {
            case SyntaxKind.Identifier:
            {
                var cloned = f.DeepCloneNode(name)!;
                e.SetEmitFlags(cloned, PrinterNs.EmitFlags.NoAsciiEscaping);
                return cloned;
            }
            case SyntaxKind.QualifiedName:
            {
                var cloned = f.DeepCloneNode(name.AsQualifiedName().Right!)!;
                e.SetEmitFlags(cloned, PrinterNs.EmitFlags.NoAsciiEscaping);
                return cloned;
            }
            default:
                return CloneBindingName(name);
        }
    }

    // nodebuilderimpl.go:1707 — cloneBindingName.
    private Node CloneBindingName(Node node)
    {
        if (NodeTests.IsComputedPropertyName(node) && ch.IsLateBindableName(node))
        {
            TrackComputedName(node.Expression()!, ctx!.enclosingDeclaration);
        }
        cloneBindingNameVisitor ??= new NodeVisitor(n => CloneBindingName(n), f, default);
        var visited = cloneBindingNameVisitor.VisitEachChild(node)!;
        if (NodeTests.IsBindingElement(visited))
        {
            var bindingElement = visited.AsBindingElement();
            visited = f.UpdateBindingElement(
                bindingElement,
                bindingElement.DotDotDotToken,
                bindingElement.PropertyName,
                bindingElement.Name(),
                null /*remove initializer*/);
        }
        if (!AstUtilities.NodeIsSynthesized(visited))
        {
            visited = f.DeepCloneNode(visited)!;
        }
        e.SetEmitFlags(visited, PrinterNs.EmitFlags.SingleLine | PrinterNs.EmitFlags.NoAsciiEscaping);
        return visited;
    }

    // nodebuilderimpl.go:1739 — serializeInferredReturnTypeForSignature.
    private Node SerializeInferredReturnTypeForSignature(Signature signature, Type returnType)
    {
        var oldSuppressReportInferenceFallback = ctx!.suppressReportInferenceFallback;
        ctx.suppressReportInferenceFallback = true;
        var typePredicate = ch.GetTypePredicateOfSignature(signature);
        Node returnTypeNode;
        if (typePredicate is not null)
        {
            TypePredicate predicate;
            if (ctx.mapper is not null)
            {
                predicate = ch.InstantiateTypePredicate(typePredicate, ctx.mapper);
            }
            else
            {
                predicate = typePredicate;
            }
            returnTypeNode = TypePredicateToTypePredicateNodeHelper(predicate);
        }
        else
        {
            returnTypeNode = TypeToTypeNodeImpl(returnType);
        }
        ctx.suppressReportInferenceFallback = oldSuppressReportInferenceFallback;
        return returnTypeNode;
    }

    // nodebuilderimpl.go:1759 — typePredicateToTypePredicateNodeHelper.
    private Node TypePredicateToTypePredicateNodeHelper(TypePredicate typePredicate)
    {
        Node? assertsModifier;
        if (typePredicate.kind == TypePredicateKind.AssertsThis || typePredicate.kind == TypePredicateKind.AssertsIdentifier)
        {
            assertsModifier = f.NewToken(SyntaxKind.AssertsKeyword);
        }
        else
        {
            assertsModifier = null;
        }
        Node parameterName;
        if (typePredicate.kind == TypePredicateKind.Identifier || typePredicate.kind == TypePredicateKind.AssertsIdentifier)
        {
            parameterName = NewIdentifier(typePredicate.parameterName, null /*symbol*/);
            e.SetEmitFlags(parameterName, PrinterNs.EmitFlags.NoAsciiEscaping);
        }
        else
        {
            parameterName = f.NewThisTypeNode();
        }
        Node? typeNode = null;
        if (typePredicate.t is not null)
        {
            typeNode = TypeToTypeNodeImpl(typePredicate.t);
        }
        return f.NewTypePredicateNode(assertsModifier, parameterName, typeNode);
    }

    // nodebuilderimpl.go:1994 — tryGetThisParameterDeclaration.
    private Node? TryGetThisParameterDeclaration(Signature signature)
    {
        if (signature.thisParameter is not null)
        {
            return SymbolToParameterDeclaration(signature.thisParameter, false);
        }
        if (signature.declaration is not null && AstUtilities.IsInJSFile(signature.declaration))
        {
            // !!! JSDoc Support
            // thisTag := getJSDocThisTag(signature.declaration)
            // if (thisTag && thisTag.typeExpression) { ... }
        }
        return null;
    }

    /**
    * Serializes the return type of the signature by first trying to use the syntactic printer if possible and falling back to the checker type if not.
     */
    // nodebuilderimpl.go:2017 — serializeReturnTypeForSignature.
    // PORT-NOTE (deferred reuse): the pseudo-checker fast path (pc.GetReturnTypeOfSignature +
    // pseudoTypeEquivalentToType, nodebuilderimpl.go:2040-2058) is owned by the node-reuse wave; the
    // guard falls through to serializeInferredReturnTypeForSignature, which is what the Go code emits
    // when the pseudo type yields no node (SerializeTypeForDeclarationImpl precedent).
    private Node? SerializeReturnTypeForSignature(Signature signature, bool tryReuse)
    {
        var suppressAny = (ctx!.flags & global::Ts.NodeBuilder.Flags.SuppressAnyReturnType) != 0;
        var restoreFlags = SaveRestoreFlags();
        if (suppressAny)
        {
            ctx.flags &= ~global::Ts.NodeBuilder.Flags.SuppressAnyReturnType; // suppress only toplevel `any`s
        }
        Node? returnTypeNode = null;

        Type? returnType;
        if (signature.declaration is not null && !AstUtilities.NodeIsSynthesized(signature.declaration))
        {
            var symbol = ch.GetSymbolOfDeclaration(signature.declaration);
            if (!ctx.enclosingSymbolTypes.TryGetValue(AstUtilities.GetSymbolId(symbol!), out returnType) || returnType is null)
            {
                returnType = ch.InstantiateType(ch.GetReturnTypeOfSignature(signature), ctx.mapper);
            }
        }
        else
        {
            returnType = ch.GetReturnTypeOfSignature(signature);
        }
        if (!(suppressAny && returnType is not null && (returnType.flags & TypeFlags.Any) != 0))
        {
            if (!IsActivelyExpanding() && tryReuse && ctx.enclosingDeclaration != null && signature.declaration != null && !AstUtilities.NodeIsSynthesized(signature.declaration))
            {
                var declarationSymbol = ch.GetSymbolOfDeclaration(signature.declaration);
                var restore = AddSymbolTypeToContext(declarationSymbol!, returnType!);
                var pt = pc.GetReturnTypeOfSignature(signature.declaration);
                if (PseudoTypeEquivalentToType(pt, returnType, false, !ctx.suppressReportInferenceFallback))
                {
                    // Also verify the pseudo type captures any inferred type predicate, not just the boolean
                    // return type. The pseudochecker is unaware of inferred type predicates, so it produces
                    // boolean where the checker infers e.g. `x is string`.
                    var typePredicate = ch.GetTypePredicateOfSignature(signature);
                    if (typePredicate != null && !PseudoReturnTypeMatchesPredicate(pt, typePredicate))
                    {
                        if (!ctx.suppressReportInferenceFallback)
                        {
                            ctx.tracker?.ReportInferenceFallback(signature.declaration);
                        }
                        pt = null;
                    }
                    if (pt != null)
                    {
                        // !!! TODO: If annotated type node is a reference with insufficient type arguments,
                        // we should still fall back to type serialization
                        // see: canReuseTypeNodeAnnotation in strada for context
                        returnTypeNode = PseudoTypeToNodeWithCheckerFallback(pt, returnType!);
                    }
                }
                restore();
            }
            if (returnTypeNode is null)
            {
                returnTypeNode = SerializeInferredReturnTypeForSignature(signature, returnType!);
            }
        }

        if (returnTypeNode is null && !suppressAny)
        {
            returnTypeNode = f.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }
        restoreFlags();
        return returnTypeNode;
    }
}

public partial class Checker
{
    // nodebuilderimpl.go:1906 — getExpandedParameters (Checker receiver; lives here to keep the Go file
    // mapping — nodebuilderimpl.go slices land in NodeBuilder/*.cs).
    internal Symbol[][] GetExpandedParameters(Signature sig, bool skipUnionExpanding)
    {
        if (SignatureHasRestParameter(sig))
        {
            var restIndex = sig.parameters!.Length - 1;
            var restSymbol = sig.parameters[restIndex];
            var restType = GetTypeOfSymbol(restSymbol);
            string[] GetUniqAssociatedNamesFromTupleType(Type t, Symbol restSymbol2)
            {
                var names = CoreUtil.MapIndex(t.Target().AsTupleType().elementInfos, (TupleElementInfo info, int i) =>
                    GetTupleElementLabel(info, restSymbol2, i)) ?? [];
                if (names.Length > 0)
                {
                    var duplicates = new List<int>();
                    var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < names.Length; i++)
                    {
                        if (!uniqueNames.Add(names[i]))
                        {
                            duplicates.Add(i);
                        }
                    }
                    var counters = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var i in duplicates)
                    {
                        if (!counters.TryGetValue(names[i], out var counter))
                        {
                            counter = 1;
                        }
                        string name;
                        while (true)
                        {
                            name = names[i] + "_" + counter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            if (uniqueNames.Contains(name))
                            {
                                counter++;
                                continue;
                            }
                            uniqueNames.Add(name);
                            break;
                        }
                        names[i] = name;
                        counters[names[i]] = counter + 1;
                    }
                }
                return names;
            }
            Symbol[] ExpandSignatureParametersWithTupleMembers(Type restType2, int restIndex2, Symbol restSymbol2)
            {
                var elementTypes = GetTypeArguments(restType2);
                var associatedNames = GetUniqAssociatedNamesFromTupleType(restType2, restSymbol2);
                var restParams = CoreUtil.MapIndex(elementTypes, (Type t, int i) =>
                {
                    // Lookup the label from the individual tuple passed in before falling back to the signature `rest` parameter name
                    // TODO: getTupleElementLabel can no longer fail, investigate if this lack of falliability meaningfully changes output
                    var name = associatedNames[i];
                    var flags = restType2.Target().AsTupleType().elementInfos![i].flags;
                    CheckFlags checkFlags = 0;
                    if ((flags & ElementFlags.Variable) != 0)
                    {
                        checkFlags = CheckFlags.RestParameter;
                    }
                    else if ((flags & ElementFlags.Optional) != 0)
                    {
                        checkFlags = CheckFlags.OptionalParameter;
                    }
                    var symbol = NewSymbolEx(SymbolFlags.FunctionScopedVariable, name, checkFlags);
                    var links = valueSymbolLinks.Get(symbol);
                    if ((flags & ElementFlags.Rest) != 0)
                    {
                        links.resolvedType = CreateArrayType(t);
                    }
                    else
                    {
                        links.resolvedType = t;
                    }
                    return symbol;
                });
                return CoreUtil.Concatenate(sig.parameters[0..restIndex2], restParams)!;
            }

            if (IsTupleType(restType))
            {
                return [ExpandSignatureParametersWithTupleMembers(restType, restIndex, restSymbol)];
            }
            else if (!skipUnionExpanding && (restType.flags & TypeFlags.Union) != 0 && CoreUtil.Every(restType.AsUnionType().types, IsTupleType))
            {
                return CoreUtil.Map(restType.AsUnionType().types, t => ExpandSignatureParametersWithTupleMembers(t, restIndex, restSymbol))!;
            }
        }
        return [sig.parameters ?? []];
    }
}
