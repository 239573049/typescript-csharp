// Additive public re-exports consumed by the Ts.LanguageService code-actions port
// (internal/ls/codeactions*.go → Ls/CodeActions*.cs).
//
// DEVIATION-REGISTER (AGENTS.md §6, logged in the port report): the dispatcher's
// parallel-discipline rule scoped this session to new Ls/CodeActions* files only, but the
// Go files call the checker package's exported (capitalized) API — exports.go — which the
// C# checker ported so far as internal members (they had no external consumer yet). Every
// member below is a 1:1 passthrough to an existing internal implementation: no logic, no
// behavior change, additive file (zero merge surface with concurrent agents). This matches
// the established convention of prior LS port commits, which publicized checker wrappers
// in the same change (e.g. 9109b7cdf "Checker.ResolveExternalModuleName 1-arg 公有 wrapper",
// 4c1c94bc8 "Checker.TryFindAmbientModule 1-arg 公有 wrapper"). Members keep their Go
// exports.go names; extension-method shape is legal next to the same-named internal
// instance members and preserves the Go call form at the call site.

using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Checker;

// ── partial Checker additions (only for members whose core is private) ─────────────────────
public sealed partial class Checker
{
    // Checker.TypeToString.cs CreateTypeToStringEmitContext is private; the code-actions
    // TypeToTypeNodeEx wrapper below needs the same request-scoped emit context.
    internal static global::Ts.Printer.EmitContext NewTypeToStringEmitContext() => CreateTypeToStringEmitContext();

    // exports.go / checker.go isTypeUsableAsPropertyName — private core landed at
    // Checker.Flow.Support.cs; Ex suffix avoids the CS0102 clash with the private member.
    public static bool IsTypeUsableAsPropertyNameEx(global::Ts.Checker.Type t) => IsTypeUsableAsPropertyName(t);

    // exports.go / checker.go getPropertyNameFromType — private core landed at
    // Checker.Flow.Support.cs; Ex suffix avoids the CS0102 clash with the private member.
    public static string GetPropertyNameFromTypeEx(global::Ts.Checker.Type t) => GetPropertyNameFromType(t);
}

// ── instance-API re-exports (extension form; internals keep their names) ────────────────────
public static class CheckerLsExports
{
    // exports.go:9 — GetStringType.
    public static global::Ts.Checker.Type? GetStringType(this Checker c) => c.stringType;

    // exports.go:13 — GetNumberType.
    public static global::Ts.Checker.Type? GetNumberType(this Checker c) => c.numberType;

    // exports.go:25 — GetUndefinedType.
    public static global::Ts.Checker.Type? GetUndefinedType(this Checker c) => c.undefinedType;

    // exports.go:353 — GetWidenedType.
    public static global::Ts.Checker.Type GetWidenedType(this Checker c, global::Ts.Checker.Type t) =>
        c.GetWidenedType(t);

    // exports.go:220 — GetReturnTypeOfSignature.
    public static global::Ts.Checker.Type GetReturnTypeOfSignature(this Checker c, Signature sig) =>
        c.GetReturnTypeOfSignature(sig);

    // exports.go:208 — GetTypePredicateOfSignature.
    public static TypePredicate? GetTypePredicateOfSignature(this Checker c, Signature sig) =>
        c.GetTypePredicateOfSignature(sig);

    // exports.go:325 — GetWidenedLiteralType.
    public static global::Ts.Checker.Type GetWidenedLiteralType(this Checker c, global::Ts.Checker.Type t) =>
        c.GetWidenedLiteralType(t);

    // exports.go:329 — IsTypeAssignableTo.
    public static bool IsTypeAssignableTo(this Checker c, global::Ts.Checker.Type source, global::Ts.Checker.Type target) =>
        c.IsTypeAssignableTo(source, target);

    // exports.go:333 — GetUnionTypeEx.
    public static global::Ts.Checker.Type GetUnionTypeEx(this Checker c, global::Ts.Checker.Type[]? types, UnionReduction unionReduction) =>
        c.GetUnionTypeEx(types, unionReduction, null /*alias*/, null /*origin*/);

    // exports.go:301 — GetTypeArguments.
    public static global::Ts.Checker.Type[] GetTypeArguments(this Checker c, global::Ts.Checker.Type t) =>
        c.GetTypeArguments(t);

    // exports.go:317 — FillMissingTypeArguments.
    public static global::Ts.Checker.Type[]? FillMissingTypeArguments(
        this Checker c, global::Ts.Checker.Type[]? typeArguments, global::Ts.Checker.Type[]? typeParameters,
        int minTypeArgumentCount, bool isJavaScriptImplicitAny) =>
        c.FillMissingTypeArguments(typeArguments, typeParameters, minTypeArgumentCount, isJavaScriptImplicitAny);

    // exports.go:261 — GetResolvedSymbol (Go getResolvedSymbol; C# core landed as
    // GetResolvedSymbolCore in Checker.OperatorExpressions.cs).
    public static global::Ts.Ast.Symbol GetResolvedSymbol(this Checker c, global::Ts.Ast.Node node) =>
        c.GetResolvedSymbolCore(node);

    // exports.go:265 — GetJsxNamespace.
    public static string GetJsxNamespace(this Checker c, global::Ts.Ast.Node? location) =>
        c.GetJsxNamespace(location);

    // exports.go:269-275 — GetJsxFragmentFactory.
    public static string GetJsxFragmentFactory(this Checker c, global::Ts.Ast.Node location)
    {
        var entity = c.GetJsxFragmentFactoryEntity(location);
        if (entity != null)
        {
            return Checker.GetFirstIdentifier(entity).Text();
        }
        return "";
    }

    // exports.go:277 — ResolveName (4-arg public shape; forwards with
    // nameNotFoundMessage=nil, isUse=true per the Go wrapper).
    public static global::Ts.Ast.Symbol? ResolveName(this Checker c, string name, global::Ts.Ast.Node? location, global::Ts.Ast.SymbolFlags meaning, bool excludeGlobals) =>
        c.ResolveName(location, name, meaning, null /*nameNotFoundMessage*/, true /*isUse*/, excludeGlobals);

    // exports.go:81 — GetNameTypeOfSymbol.
    public static global::Ts.Checker.Type? GetNameTypeOfSymbol(this Checker c, global::Ts.Ast.Symbol symbol)
    {
        if (!c.valueSymbolLinks.Has(symbol))
        {
            return null;
        }
        return c.valueSymbolLinks.Get(symbol).nameType;
    }

    // exports.go:305 — GetIndexInfoOfType.
    public static IndexInfo? GetIndexInfoOfType(this Checker c, global::Ts.Checker.Type t, global::Ts.Checker.Type keyType) =>
        c.GetIndexInfoOfType(t, keyType);

    // printer.go:478 — TypeToTypeNodeEx: request-scoped builder over the idToSymbol side
    // table the caller owns (filled in place by the builder, as in Go).
    public static global::Ts.Ast.Node? TypeToTypeNodeEx(
        this Checker c, global::Ts.Checker.Type t, global::Ts.Ast.Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        Dictionary<global::Ts.Ast.Node, global::Ts.Ast.Symbol> idToSymbol)
    {
        var nodeBuilder = new NodeBuilder(c, Checker.NewTypeToStringEmitContext(), idToSymbol);
        return nodeBuilder.TypeToTypeNode(t, enclosingDeclaration, flags, internalFlags, null /*tracker*/);
    }

    // printer.go:473 — TypePredicateToTypePredicateNode.
    public static global::Ts.Ast.Node? TypePredicateToTypePredicateNode(
        this Checker c, TypePredicate typePredicate, global::Ts.Ast.Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, Dictionary<global::Ts.Ast.Node, global::Ts.Ast.Symbol>? idToSymbol)
    {
        var nodeBuilder = new NodeBuilder(c, Checker.NewTypeToStringEmitContext(), idToSymbol);
        return nodeBuilder.TypePredicateToTypePredicateNode(typePredicate, enclosingDeclaration, flags, global::Ts.NodeBuilder.InternalFlags.None, null /*tracker*/);
    }

    // ── NodeBuilder seam (nodebuilder.go NewNodeBuilderEx + request-builder entry points) ────

    // nodebuilder.go:283 — NewNodeBuilderEx.
    public static NodeBuilder NewNodeBuilderEx(this Checker ch, global::Ts.Printer.EmitContext e, Dictionary<global::Ts.Ast.Node, global::Ts.Ast.Symbol>? idToSymbol) =>
        new(ch, e, idToSymbol);

    // nodebuilder.go — SignatureToSignatureDeclaration (5-arg public shape; tracker=nil).
    public static global::Ts.Ast.Node? SignatureToSignatureDeclaration(
        this NodeBuilder nodeBuilder, Signature signature, global::Ts.Ast.SyntaxKind kind,
        global::Ts.Ast.Node? enclosingDeclaration, global::Ts.NodeBuilder.Flags flags,
        global::Ts.NodeBuilder.InternalFlags internalFlags) =>
        nodeBuilder.SignatureToSignatureDeclaration(signature, kind, enclosingDeclaration, flags, internalFlags, null /*tracker*/);

    // nodebuilder.go:111 — IndexInfoToIndexSignatureDeclaration (4-arg public shape;
    // tracker=nil as in Go — the internal parameter is not null-annotated, hence null!).
    public static global::Ts.Ast.Node? IndexInfoToIndexSignatureDeclaration(
        this NodeBuilder nodeBuilder, IndexInfo info, global::Ts.Ast.Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags) =>
        nodeBuilder.IndexInfoToIndexSignatureDeclaration(info, enclosingDeclaration, flags, internalFlags, null! /*tracker*/);

    // ── static package-func re-export ──────────────────────────────────────────────────────────

    // exports.go / checker.go GetSetAccessorValueParameter (internal static core in
    // Checker.MembersSignatures.Deps.cs; Checker.GetDeclarationModifierFlagsFromSymbol is
    // already public static on Checker and is consumed directly).
    public static global::Ts.Ast.Node? GetSetAccessorValueParameter(global::Ts.Ast.Node accessor) =>
        Checker.GetSetAccessorValueParameter(accessor);

    // ── printer seam ───────────────────────────────────────────────────────────────────────────

    // printer/singlelinestringwriter.go — GetSingleLineStringWriter: the concrete writer class
    // is internal to Ts.Compiler, so the LS port reaches the same Go return shape (the
    // EmitTextWriter face plus the release closure) through this passthrough. The String()
    // readback needs no seam — it is a member of the EmitTextWriter interface.
    public static (global::Ts.Printer.EmitTextWriter Writer, System.Action Release) GetSingleLineStringWriter() =>
        global::Ts.Printer.SingleLineStringWriter.GetSingleLineStringWriter();

    // ── NodeBuilder / EmitResolver seams added for the code-actions port ──────────────────────

    // nodebuilder.go — TypeToTypeNode (4-arg public face). The internal core takes the
    // request-scoped change tracker, which the LS fixers pass as none, exactly as in Go.
    public static global::Ts.Ast.Node? TypeToTypeNode(
        this NodeBuilder nodeBuilder, global::Ts.Checker.Type t, global::Ts.Ast.Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags) =>
        nodeBuilder.TypeToTypeNode(t, enclosingDeclaration, flags, internalFlags, null /*tracker*/);

    // exports.go:337 — RequiresAddingImplicitUndefined (the 1-arg Checker export; the 3-arg
    // EmitResolver core is public on Checker directly).
    public static bool RequiresAddingImplicitUndefined(this Checker c, global::Ts.Ast.Node node)
    {
        var enclosingDeclaration = global::Ts.Ast.AstUtilities.FindAncestor(node, global::Ts.Ast.AstUtilities.IsDeclaration);
        if (enclosingDeclaration == null)
        {
            enclosingDeclaration = global::Ts.Ast.AstUtilities.GetSourceFileOfNode(node)!.AsNode();
        }
        var symbol = node.Symbol();
        if (symbol == null)
        {
            return false;
    }
        return c.GetEmitResolver().RequiresAddingImplicitUndefined(node, symbol, enclosingDeclaration);
    }
}
