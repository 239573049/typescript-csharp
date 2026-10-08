// Ported from tsgo internal/ls/codeactions_missingmemberfixer.go (whole file): the
// missingMemberFixer — synthesizes the missing class members (properties, accessors,
// methods, index signatures) for the "class incorrectly implements interface" fix.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Checker; // CheckerLsExports extension methods (GetWidenedType, GetReturnTypeOfSignature, ...)
using Ts.Core;
using Ts.Diagnostics; // Message.Localize extension

namespace Ts.Ls;

// codeactions_missingmemberfixer.go:18-24 — preserveOptionalFlags controls which member kinds
// keep their optional (`?`) marker when synthesized.
[Flags]
internal enum PreserveOptionalFlags
{
    Method = 1 << 0,
    Property = 1 << 1,
    All = Method | Property,
}

// codeactions_missingmemberfixer.go:26-33 — missingMemberFixer encapsulates the state shared
// by the member-synthesis helpers.
internal sealed class MissingMemberFixer
{
    internal global::Ts.Ls.Change.Tracker changeTracker = null!;
    internal global::Ts.Checker.Checker typeChecker = null!;
    internal global::Ts.Compiler.Program program = null!;
    internal UserPreferences preferences;
    internal global::Ts.Ls.AutoImport.ImportAdder? importAdder;
    internal global::Ts.Locale.Locale locale;

    // codeactions_missingmemberfixer.go:35-44 — newMissingMemberFixer.
    internal MissingMemberFixer(global::Ts.Ls.Change.Tracker changeTracker, global::Ts.Compiler.Program program,
        global::Ts.Checker.Checker typeChecker, UserPreferences preferences,
        global::Ts.Ls.AutoImport.ImportAdder? importAdder, global::Ts.Locale.Locale locale)
    {
        this.changeTracker = changeTracker;
        this.program = program;
        this.typeChecker = typeChecker;
        this.preferences = preferences;
        this.importAdder = importAdder;
        this.locale = locale;
    }

    // codeactions_missingmemberfixer.go:46-50 — createNodeBuilder returns a fresh node builder
    // plus the idToSymbol side table the caller keeps filling through the synthesized nodes.
    internal (global::Ts.Checker.NodeBuilder NodeBuilder, Dictionary<Node, global::Ts.Ast.Symbol> IdToSymbol) CreateNodeBuilder()
    {
        var idToSymbol = new Dictionary<Node, global::Ts.Ast.Symbol>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance);
        var nodeBuilder = typeChecker.NewNodeBuilderEx(changeTracker.EmitContext, idToSymbol);
        return (nodeBuilder, idToSymbol);
    }

    // codeactions_missingmemberfixer.go:52-168 — createMemberFromSymbol synthesizes the class
    // member(s) mirroring `symbol` at `enclosingDeclaration`.
    internal Node[]? CreateMemberFromSymbol(global::Ts.Ast.Symbol symbol, Node enclosingDeclaration, SourceFile sourceFile,
        Node? body, PreserveOptionalFlags preserveOptional)
    {
        var declarations = symbol.Declarations;
        var declaration = CoreUtil.FirstOrNil(declarations);

        var quotePreference = LsDeps.GetQuotePreference(sourceFile, preferences);
        var ambient = (enclosingDeclaration.Flags & NodeFlags.Ambient) != 0;
        var optional = (symbol.Flags & SymbolFlags.Optional) != 0;
        var kind = SyntaxKind.PropertySignature;
        if (declaration != null)
        {
            kind = declaration.Kind;
        }
        var declarationName = CodeActions.CreateDeclarationName(changeTracker.NodeFactory, typeChecker, symbol, declaration);
        var modifiers = CreateModifiers(symbol, declaration);

        var flags = global::Ts.NodeBuilder.Flags.NoTruncation;
        if (quotePreference == QuotePreference.Single)
        {
            flags |= global::Ts.NodeBuilder.Flags.UseSingleQuotesForStringLiteralType;
        }

        var t = typeChecker.GetWidenedType(typeChecker.GetTypeOfSymbolAtLocation(symbol, enclosingDeclaration));
        var nodes = new List<Node>();

        switch (kind)
        {
            case SyntaxKind.PropertySignature:
            case SyntaxKind.PropertyDeclaration:
            {
                var (nodeBuilder, idToSymbol) = CreateNodeBuilder();
                var typeNode = CreateTypeNode(t, enclosingDeclaration, flags, nodeBuilder, idToSymbol);
                Node? questionToken = null;
                if (optional && (preserveOptional & PreserveOptionalFlags.Property) != 0)
                {
                    questionToken = changeTracker.NodeFactory.NewToken(SyntaxKind.QuestionToken);
                }
                return [changeTracker.NodeFactory.NewPropertyDeclaration(modifiers, CodeActions.CreatePropertyName(changeTracker.NodeFactory, declarationName!, quotePreference), questionToken, typeNode, null /*initializer*/)];
            }

            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            {
                var (nodeBuilder, idToSymbol) = CreateNodeBuilder();
                var accessors = AstUtilities.GetAllAccessorDeclarations(declarations ?? [], declaration!);
                var orderedAccessors = new List<Node>();
                if (accessors.SecondAccessor == null)
                {
                    orderedAccessors.Add(accessors.FirstAccessor!);
                }
                else
                {
                    orderedAccessors.Add(accessors.FirstAccessor!);
                    orderedAccessors.Add(accessors.SecondAccessor);
                }

                foreach (var accessor in orderedAccessors)
                {
                    if (NodeTests.IsGetAccessorDeclaration(accessor))
                    {
                        nodes.Add(changeTracker.NodeFactory.NewGetAccessorDeclaration(
                            modifiers, CodeActions.CreatePropertyName(changeTracker.NodeFactory, declarationName!, quotePreference),
                            null /*typeParameters*/, null /*parameters*/, CreateTypeNode(t, enclosingDeclaration, flags, nodeBuilder, idToSymbol), null /*fullSignature*/, CreateBody(body, ambient, quotePreference)));
                    }

                    if (NodeTests.IsSetAccessorDeclaration(accessor))
                    {
                        var parameter = global::Ts.Checker.CheckerLsExports.GetSetAccessorValueParameter(accessor);
                        if (parameter == null)
                        {
                            throw new InvalidOperationException("Expected set accessor to have a parameter.");
                        }

                        nodes.Add(changeTracker.NodeFactory.NewSetAccessorDeclaration(
                            modifiers, CodeActions.CreatePropertyName(changeTracker.NodeFactory, declarationName!, quotePreference),
                            null /*typeParameters*/, CodeActions.CreateDummyParameters(changeTracker.NodeFactory, 1, [parameter.Name()!.Text()], [CreateTypeNode(t, enclosingDeclaration, flags, nodeBuilder, idToSymbol)], 1, AstUtilities.IsInJSFile(enclosingDeclaration)),
                            null /*type*/, null /*fullSignature*/, CreateBody(body, ambient, quotePreference)));
                    }
                }
                return nodes.ToArray();
            }

            case SyntaxKind.MethodSignature:
            case SyntaxKind.MethodDeclaration:
            {
                var signatures = GetCallSignatures(t);
                // Go shadows the `preserveOptional` parameter here.
                var preserveOptionalMethod = optional && (preserveOptional & PreserveOptionalFlags.Method) != 0;
                if (signatures.Length == 0)
                {
                    return null;
                }

                if ((declarations?.Length ?? 0) == 1)
                {
                    var method = CreateSignatureDeclarationFromSignature(CoreUtil.FirstOrNil(signatures)!, SyntaxKind.MethodDeclaration, sourceFile, enclosingDeclaration, CreateBody(body, ambient, quotePreference), modifiers, declarationName, preserveOptionalMethod);
                    if (method != null)
                    {
                        nodes.Add(method);
                    }
                    return nodes.ToArray();
                }

                foreach (var signature in signatures)
                {
                    if (signature.Declaration() != null && (signature.Declaration()!.Flags & NodeFlags.Ambient) != 0)
                    {
                        continue;
                    }

                    var method = CreateSignatureDeclarationFromSignature(signature, SyntaxKind.MethodDeclaration, sourceFile, enclosingDeclaration, null, modifiers, declarationName, preserveOptionalMethod);
                    if (method != null)
                    {
                        nodes.Add(method);
                    }
                }

                if (ambient)
                {
                    return nodes.ToArray();
                }

                if ((declarations?.Length ?? 0) > signatures.Length)
                {
                    var signature = typeChecker.GetSignatureFromDeclaration(CoreUtil.LastOrNil(declarations)!);
                    var method = CreateSignatureDeclarationFromSignature(signature, SyntaxKind.MethodDeclaration, sourceFile, enclosingDeclaration, CreateBody(body, ambient, quotePreference), modifiers, declarationName, preserveOptionalMethod);
                    if (method != null)
                    {
                        nodes.Add(method);
                    }
                }
                else
                {
                    var method = CreateSignatureDeclarationFromSignatures(signatures, declarationName, preserveOptionalMethod, modifiers, quotePreference, body, enclosingDeclaration);
                    if (method != null)
                    {
                        nodes.Add(method);
                    }
                }

                return nodes.ToArray();
            }
        }
        return null;
    }

    // codeactions_missingmemberfixer.go:170-175 — getCallSignatures returns the call signatures
    // of `t`, flattening each union constituent.
    internal global::Ts.Checker.Signature[] GetCallSignatures(global::Ts.Checker.Type t)
    {
        if (t.IsUnion())
        {
            return CoreUtil.FlatMap(t.Types(), typeChecker.GetCallSignatures) ?? [];
        }
        return typeChecker.GetCallSignatures(t);
    }

    // codeactions_missingmemberfixer.go:177-179 — createTypeNode renders `t` as a type node and
    // routes identifiers through the import adder.
    internal Node? CreateTypeNode(global::Ts.Checker.Type t, Node enclosingDeclaration, global::Ts.NodeBuilder.Flags flags,
        global::Ts.Checker.NodeBuilder nodeBuilder, Dictionary<Node, global::Ts.Ast.Symbol> idToSymbol)
    {
        return ImportTypeNode(nodeBuilder.TypeToTypeNode(t, enclosingDeclaration, flags, global::Ts.NodeBuilder.InternalFlags.None), idToSymbol);
    }

    // codeactions_missingmemberfixer.go:181-202 — createModifiers derives the modifier list from
    // the symbol's effective modifiers: static is kept as-is, public/protected are kept, and
    // auto-accessors plus the override keyword are added when the declaration demands them.
    internal ModifierList? CreateModifiers(global::Ts.Ast.Symbol symbol, Node? declaration)
    {
        var modifierFlags = ModifierFlags.None;
        if (declaration != null)
        {
            var effective = global::Ts.Checker.Checker.GetDeclarationModifierFlagsFromSymbol(symbol);
            modifierFlags = effective & ModifierFlags.Static;
            if ((effective & ModifierFlags.Public) != 0)
            {
                modifierFlags |= ModifierFlags.Public;
            }
            else if ((effective & ModifierFlags.Protected) != 0)
            {
                modifierFlags |= ModifierFlags.Protected;
            }
            if (AstUtilities.IsAutoAccessorPropertyDeclaration(declaration))
            {
                modifierFlags |= ModifierFlags.Accessor;
            }
        }
        if (ShouldAddOverrideKeyword(declaration))
        {
            modifierFlags |= ModifierFlags.Override;
        }
        if (modifierFlags == ModifierFlags.None)
        {
            return null;
        }
        return changeTracker.NodeFactory.NewModifierList(AstUtilities.CreateModifiersFromModifierFlags(modifierFlags, changeTracker.NodeFactory.NewModifier));
    }

    // codeactions_missingmemberfixer.go:204-206 — shouldAddOverrideKeyword adds `override` when
    // noImplicitOverride is on and the overridden base member is abstract.
    internal bool ShouldAddOverrideKeyword(Node? declaration)
    {
        return declaration != null && program.Options().NoImplicitOverride!.IsTrue() && AstUtilities.HasAbstractModifier(declaration);
    }

    // codeactions_missingmemberfixer.go:208-303 — createSignatureDeclarationFromSignature builds
    // a single declaration from one signature, rewriting type references through the import
    // adder and stripping type information in JS files.
    internal Node? CreateSignatureDeclarationFromSignature(global::Ts.Checker.Signature signature, SyntaxKind kind,
        SourceFile sourceFile, Node enclosingDeclaration, Node? body, ModifierList? modifiers, Node? name, bool optional)
    {
        var quotePreference = LsDeps.GetQuotePreference(sourceFile, preferences);
        var flags = global::Ts.NodeBuilder.Flags.NoTruncation | global::Ts.NodeBuilder.Flags.SuppressAnyReturnType | global::Ts.NodeBuilder.Flags.AllowEmptyTuple;
        if (quotePreference == QuotePreference.Single)
        {
            flags |= global::Ts.NodeBuilder.Flags.UseSingleQuotesForStringLiteralType;
        }

        var (nodeBuilder, idToSymbol) = CreateNodeBuilder();
        var signatureDeclaration = nodeBuilder.SignatureToSignatureDeclaration(signature, kind, enclosingDeclaration, flags, global::Ts.NodeBuilder.InternalFlags.AllowUnresolvedNames);
        if (signatureDeclaration == null)
        {
            return null;
        }

        var isJS = AstUtilities.IsInJSFile(enclosingDeclaration);
        var parameters = signatureDeclaration.ParameterList();
        var typeParameters = CoreUtil.IfElse(isJS, (global::Ts.Ast.NodeList?)null, signatureDeclaration.TypeParameterList());
        var typeNode = CoreUtil.IfElse(isJS, (Node?)null, signatureDeclaration.Type());

        if (typeParameters != null && typeParameters.Nodes.Length > 0)
        {
            var nodes = new List<Node>(typeParameters.Nodes.Length);
            foreach (var tp in typeParameters.Nodes)
            {
                if (tp == null)
                {
                    continue;
                }

                if (NodeTests.IsTypeParameterDeclaration(tp))
                {
                    var typeParameter = tp.AsTypeParameterDeclaration();

                    var constraint = typeParameter.Constraint;
                    if (constraint != null)
                    {
                        constraint = ImportTypeNode(constraint, idToSymbol);
                    }

                    var defaultType = typeParameter.DefaultType;
                    if (defaultType != null)
                    {
                        defaultType = ImportTypeNode(defaultType, idToSymbol);
                    }

                    nodes.Add(changeTracker.NodeFactory.UpdateTypeParameterDeclaration(typeParameter, typeParameter.Modifiers(), typeParameter.Name(), constraint, typeParameter.Expression, defaultType));
                }
                else
                {
                    nodes.Add(tp);
                }
            }
            typeParameters = changeTracker.NodeFactory.NewNodeList(nodes.ToArray());
        }

        if (parameters != null)
        {
            var nodes = new List<Node>(parameters.Nodes.Length);
            foreach (var p in parameters.Nodes)
            {
                if (p == null)
                {
                    continue;
                }

                var parameter = p.AsParameterDeclaration();
                var parameterTypeNode = parameter.Type;
                if (parameterTypeNode != null)
                {
                    parameterTypeNode = ImportTypeNode(parameterTypeNode, idToSymbol);
                }

                nodes.Add(changeTracker.NodeFactory.UpdateParameterDeclaration(parameter, parameter.Modifiers(), parameter.DotDotDotToken, parameter.Name(), CoreUtil.IfElse(isJS, (Node?)null, parameter.QuestionToken), parameterTypeNode, parameter.Initializer));
            }
            parameters = changeTracker.NodeFactory.NewNodeList(nodes.ToArray());
        }

        if (typeNode != null)
        {
            typeNode = ImportTypeNode(typeNode, idToSymbol);
        }

        Node? questionToken = null;
        if (optional)
        {
            questionToken = changeTracker.NodeFactory.NewToken(SyntaxKind.QuestionToken);
        }

        switch (kind)
        {
            case SyntaxKind.FunctionExpression:
            {
                var fn = signatureDeclaration.AsFunctionExpression();
                return changeTracker.NodeFactory.UpdateFunctionExpression(fn, modifiers, fn.AsteriskToken, CoreUtil.IfElse(name != null && NodeTests.IsIdentifier(name), name, null), typeParameters, parameters, typeNode, fn.FullSignature, CoreUtil.OrElse(body, fn.Body));
            }

            case SyntaxKind.ArrowFunction:
            {
                var fn = signatureDeclaration.AsArrowFunction();
                return changeTracker.NodeFactory.UpdateArrowFunction(fn, modifiers, typeParameters, parameters, typeNode, fn.FullSignature, fn.EqualsGreaterThanToken, CoreUtil.OrElse(body, fn.Body));
            }

            case SyntaxKind.MethodDeclaration:
            {
                var method = signatureDeclaration.AsMethodDeclaration();
                var methodName = CoreUtil.IfElse(name == null, changeTracker.NodeFactory.NewIdentifier(""), CodeActions.CreatePropertyName(changeTracker.NodeFactory, name!, quotePreference));
                return changeTracker.NodeFactory.UpdateMethodDeclaration(method, modifiers, method.AsteriskToken, methodName, questionToken, typeParameters, parameters, typeNode, method.FullSignature, body);
            }

            case SyntaxKind.FunctionDeclaration:
            {
                var fn = signatureDeclaration.AsFunctionDeclaration();
                return changeTracker.NodeFactory.UpdateFunctionDeclaration(fn, modifiers, fn.AsteriskToken, CoreUtil.IfElse(name != null && NodeTests.IsIdentifier(name), name, null), typeParameters, parameters, typeNode, fn.FullSignature, CoreUtil.OrElse(body, fn.Body));
            }
        }

        return null;
    }

    // codeactions_missingmemberfixer.go:305-357 — createSignatureDeclarationFromSignatures folds
    // an overload set into one method declaration with dummy parameters covering the widest
    // signature (plus a rest parameter when any signature has one).
    internal Node? CreateSignatureDeclarationFromSignatures(global::Ts.Checker.Signature[] signatures, Node? name,
        bool optional, ModifierList? modifiers, QuotePreference quotePreference, Node? body, Node enclosingDeclaration)
    {
        if (signatures.Length == 0)
        {
            return null;
        }

        var (nodeBuilder, idToSymbol) = CreateNodeBuilder();
        var maxArgsSignature = signatures[0];
        var minArgumentCount = signatures[0].MinArgumentCount();

        var hasRestParameter = false;
        foreach (var signature in signatures)
        {
            minArgumentCount = Math.Min(minArgumentCount, signature.MinArgumentCount());
            if (signature.HasRestParameter())
            {
                hasRestParameter = true;
            }
            if (signature.Parameters().Length >= maxArgsSignature.Parameters().Length && (!signature.HasRestParameter() || maxArgsSignature.HasRestParameter()))
            {
                maxArgsSignature = signature;
            }
        }

        var maxNonRestArgs = maxArgsSignature.Parameters().Length - CoreUtil.IfElse(maxArgsSignature.HasRestParameter(), 1, 0);
        var parameterNames = new List<string>(maxArgsSignature.Parameters().Length);
        foreach (var symbol in maxArgsSignature.Parameters())
        {
            parameterNames.Add(symbol.Name);
        }
        var parameters = CodeActions.CreateDummyParameters(changeTracker.NodeFactory, maxNonRestArgs, parameterNames.ToArray(), null /*types*/, minArgumentCount, AstUtilities.IsInJSFile(enclosingDeclaration));

        if (hasRestParameter)
        {
            var restParameterName = "rest";
            if (maxNonRestArgs < parameterNames.Count && parameterNames[maxNonRestArgs] != "")
            {
                restParameterName = parameterNames[maxNonRestArgs];
            }

            Node? questionToken = null;
            if (maxNonRestArgs >= minArgumentCount)
            {
                questionToken = changeTracker.NodeFactory.NewToken(SyntaxKind.QuestionToken);
            }

            // Go appends to parameters.Nodes in place; the C# NodeList.Nodes is a fixed array,
            // so rebuild it with the rest parameter appended.
            var restParameter = changeTracker.NodeFactory.NewParameterDeclaration(
                null /*modifiers*/, changeTracker.NodeFactory.NewToken(SyntaxKind.DotDotDotToken),
                changeTracker.NodeFactory.NewIdentifier(restParameterName), questionToken,
                changeTracker.NodeFactory.NewArrayTypeNode(changeTracker.NodeFactory.NewKeywordTypeNode(SyntaxKind.UnknownKeyword)), null /*initializer*/);
            parameters.Nodes = [.. parameters.Nodes, restParameter];
        }

        var methodName = CoreUtil.IfElse(name == null, changeTracker.NodeFactory.NewIdentifier(""), CodeActions.CreatePropertyName(changeTracker.NodeFactory, name!, quotePreference));

        return changeTracker.NodeFactory.NewMethodDeclaration(
            modifiers, null /*asteriskToken*/, methodName, CoreUtil.IfElse(optional, changeTracker.NodeFactory.NewToken(SyntaxKind.QuestionToken), (Node?)null),
            null /*typeParameters*/, parameters, GetReturnTypeFromSignatures(signatures, enclosingDeclaration, nodeBuilder, idToSymbol),
            null /*fullSignature*/, CreateBody(body, false /*ambient*/, quotePreference));
    }

    // codeactions_missingmemberfixer.go:359-371 — getReturnTypeFromSignatures unions the return
    // types of all signatures and renders the union.
    internal Node? GetReturnTypeFromSignatures(global::Ts.Checker.Signature[] signatures, Node enclosingDeclaration,
        global::Ts.Checker.NodeBuilder nodeBuilder, Dictionary<Node, global::Ts.Ast.Symbol> idToSymbol)
    {
        if (signatures.Length == 0)
        {
            return null;
        }

        var returnTypes = new List<global::Ts.Checker.Type>(signatures.Length);
        foreach (var signature in signatures)
        {
            returnTypes.Add(typeChecker.GetReturnTypeOfSignature(signature));
        }

        var unionType = typeChecker.GetUnionType(returnTypes.ToArray());
        return ImportTypeNode(nodeBuilder.TypeToTypeNode(unionType, enclosingDeclaration, global::Ts.NodeBuilder.Flags.NoTruncation, global::Ts.NodeBuilder.InternalFlags.AllowUnresolvedNames), idToSymbol);
    }

    // codeactions_missingmemberfixer.go:373-395 — importTypeNode swaps auto-importable type
    // references for import types and queues the corresponding imports; otherwise it falls back
    // to importing every symbol the node builder resolved through idToSymbol.
    internal Node? ImportTypeNode(Node? typeNode, Dictionary<Node, global::Ts.Ast.Symbol> idToSymbol)
    {
        if (typeNode == null || importAdder == null)
        {
            return typeNode;
        }

        var (importedTypeNode, symbols) = global::Ts.Ls.AutoImport.AutoImportApi.TryGetAutoImportableReferenceFromTypeNode(typeNode, idToSymbol);
        if (importedTypeNode != null)
        {
            foreach (var symbol in symbols)
            {
                importAdder.AddImportFromExportedSymbol(symbol, true /*isValidTypeOnlyUseSite*/);
            }
            return importedTypeNode;
        }

        // map-iter: 序无关 — dedup pass over the idToSymbol values; each distinct symbol gets an
        // import queued, and the adder is order-independent.
        var seen = new HashSet<global::Ts.Ast.Symbol>(global::Ts.Core.ReferenceEqualityComparer<global::Ts.Ast.Symbol>.Instance);
        foreach (var symbol in idToSymbol.Values)
        {
            if (symbol == null || seen.Contains(symbol))
            {
                continue;
            }
            seen.Add(symbol);
            importAdder.AddImportFromExportedSymbol(symbol, true /*isValidTypeOnlyUseSite*/);
        }
        return typeNode;
    }

    // codeactions_missingmemberfixer.go:397-405 — createIndexSignatureDeclarationFromType renders
    // the index signature of `implementedType` for `keyType`, if it has one.
    internal Node? CreateIndexSignatureDeclarationFromType(Node classDeclaration, global::Ts.Checker.Type implementedType, global::Ts.Checker.Type keyType)
    {
        var indexInfo = typeChecker.GetIndexInfoOfType(implementedType, keyType);
        if (indexInfo == null)
        {
            return null;
        }

        var builder = typeChecker.NewNodeBuilderEx(changeTracker.EmitContext, null);
        return builder.IndexInfoToIndexSignatureDeclaration(indexInfo, classDeclaration, global::Ts.NodeBuilder.Flags.None, global::Ts.NodeBuilder.InternalFlags.None);
    }

    // codeactions_missingmemberfixer.go:407-416 — createBody clones the implemented member's
    // body for the synthesized member; ambient members get none.
    internal Node? CreateBody(Node? body, bool ambient, QuotePreference quotePreference)
    {
        if (ambient)
        {
            return null;
        }
        body = changeTracker.NodeFactory.DeepCloneNode(body!);
        if (body == null)
        {
            return CreateStubbedMethodBody(quotePreference);
        }
        return body;
    }

    // codeactions_missingmemberfixer.go:418-433 — createStubbedMethodBody builds
    // `throw new Error("Method not implemented.")`.
    internal Node CreateStubbedMethodBody(QuotePreference quotePreference)
    {
        var tokenFlags = TokenFlags.None;
        if (quotePreference == QuotePreference.Single)
        {
            tokenFlags = TokenFlags.SingleQuote;
        }

        return changeTracker.NodeFactory.NewBlock(changeTracker.NodeFactory.NewNodeList(
        [
            changeTracker.NodeFactory.NewThrowStatement(
                changeTracker.NodeFactory.NewNewExpression(
                    changeTracker.NodeFactory.NewIdentifier("Error"), null /*typeArguments*/, changeTracker.NodeFactory.NewNodeList(
                    [
                        changeTracker.NodeFactory.NewStringLiteral(global::Ts.Diagnostics.Diagnostics.Method_not_implemented.Localize(locale), tokenFlags),
                        // PORT-NOTE: Go writes the closing as `}),` with a trailing argument-list
                        // comma, which C# rejects — the comma is dropped here.
                    ]))
            ),
        ]), true /*multiLine*/);
    }
}

// Package-level helpers of codeactions_missingmemberfixer.go (Go package funcs → static
// partial class, mirroring the other CodeActions* port files).
public static partial class CodeActions
{
    // codeactions_missingmemberfixer.go:435-471 — createDummyParameters builds `argCount`
    // positional parameters, deduplicating collisions with a numeric suffix and marking
    // parameters past minArgumentCount optional.
    internal static NodeList CreateDummyParameters(NodeFactory factory, int argCount, string[]? names, Node?[]? types, int minArgumentCount, bool inJS)
    {
        var parameters = new List<Node>(argCount);
        var parameterNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < argCount; i++)
        {
            var parameterName = "";
            if (i < (names?.Length ?? 0) && names![i] != "")
            {
                parameterName = names[i];
            }
            else
            {
                parameterName = "arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var count = parameterNameCounts.GetValueOrDefault(parameterName);
            parameterNameCounts[parameterName] = count + 1;

            if (count > 0)
            {
                parameterName += count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            Node? questionToken = null;
            if (i >= minArgumentCount)
            {
                questionToken = factory.NewToken(SyntaxKind.QuestionToken);
            }

            Node? typeNode;
            if (inJS)
            {
                typeNode = null;
            }
            else if (i < (types?.Length ?? 0) && types![i] != null)
            {
                typeNode = types[i];
            }
            else
            {
                typeNode = factory.NewKeywordTypeNode(SyntaxKind.UnknownKeyword);
            }
            parameters.Add(factory.NewParameterDeclaration(null /*modifiers*/, null /*dotDotDotToken*/, factory.NewIdentifier(parameterName), questionToken, typeNode, null /*initializer*/));
        }
        return factory.NewNodeList(parameters.ToArray());
    }

    // codeactions_missingmemberfixer.go:473-487 — createDeclarationName picks the best property
    // name for the synthesized member: the mapped type's name type, the declaration's name, or
    // the symbol name.
    internal static Node? CreateDeclarationName(NodeFactory factory, global::Ts.Checker.Checker typeChecker, global::Ts.Ast.Symbol? symbol, Node? declaration)
    {
        if (symbol != null && (symbol.CheckFlags & CheckFlags.Mapped) != 0)
        {
            var nameType = typeChecker.GetNameTypeOfSymbol(symbol);
            if (nameType != null && global::Ts.Checker.Checker.IsTypeUsableAsPropertyNameEx(nameType))
            {
                return factory.NewIdentifier(global::Ts.Checker.Checker.GetPropertyNameFromTypeEx(nameType));
            }
        }
        if (declaration != null && declaration.Name() != null)
        {
            return declaration.Name()!.Clone(factory);
        }
        if (symbol != null)
        {
            return factory.NewIdentifier(symbol.Name);
        }
        return null;
    }

    // codeactions_missingmemberfixer.go:489-498 — createPropertyName clones the name node,
    // computing `"constructor"` so it does not collide with the class constructor.
    internal static Node? CreatePropertyName(NodeFactory factory, Node? node, QuotePreference quotePreference)
    {
        if (NodeTests.IsIdentifier(node!) && node!.Text() == "constructor")
        {
            var tokenFlags = TokenFlags.None;
            if (quotePreference == QuotePreference.Single)
            {
                tokenFlags = TokenFlags.SingleQuote;
            }
            return factory.NewComputedPropertyName(factory.NewStringLiteral(node!.Text(), tokenFlags));
        }
        return factory.DeepCloneNode(node!);
    }
}
