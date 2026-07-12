// Ported from tsgo internal/checker/printer.go (typeToString / typeToStringEx family) and the
// intrinsic/literal/union/intersection/type-reference slice of internal/checker/nodebuilderimpl.go
// (typeToTypeNode). This is the checker's Type -> error-message-string serializer:
//   typeToStringEx  =  nodeBuilder.TypeToTypeNode(t)  ->  emit Printer prints the TypeNode to text.
//
// SCOPE (breadth-first, per phase-5 type-display wave):
//   The full NodeBuilder/NodeBuilderImpl (internal/checker/nodebuilderimpl.go, ~150 KB) is deferred.
//   This wave ports only the type kinds needed by common assignability error messages so the checker
//   can emit its first end-to-end "Type 'X' is not assignable to type 'Y'" diagnostic:
//     * intrinsics: any / unknown / string / number / bigint / boolean / void / undefined / null /
//                   never / symbol / object (+ the `intrinsic` marker keyword)
//     * literals:   string / number / bigint / boolean literal types
//     * composite:  union / intersection (constituent mapping)
//     * named:      a minimal symbol-name type reference for named / enum-like types
//   All other kinds (conditional, mapped, indexed access, template literal, anonymous object,
//   type parameters with constraints, full symbol chains, reference type-argument expansion,
//   truncation of long constituent lists, VerbosityContext hover expansion) fall through to
//   TypeToTypeNodeFallback and render as the `any` keyword, each marked with a PORT-NOTE. They land
//   with the full NodeBuilderImpl port in a later wave.

using System;
using Ts.Ast;
using Ts.Core;

using PrinterNs = global::Ts.Printer;

namespace Ts.Checker;

public sealed partial class Checker
{
    // nodebuilderimpl.go:113
    internal const int DefaultMaximumTruncationLength = 160;
    // nodebuilderimpl.go:114
    internal const int NoTruncationMaximumTruncationLength = 1_000_000;

    // printer.go:181 — toNodeBuilderFlags
    private static PrinterNs.EmitContext CreateTypeToStringEmitContext() => PrinterNs.EmitContext.NewEmitContext();

    // printer.go:189 — typeToStringEx.
    //
    // Serialization of types can lead to (lazy) resolution of members, which can cause diagnostics that
    // again require serialization of types. This can potentially result in infinite recursion and stack
    // overflows. To prevent that, after a certain number of recursive invocations the function simply
    // returns "?". (The serializationLevel guard is load-bearing: addDiagnostic drops diagnostics while
    // at the maximum recursion level — see Checker.SymbolResolution.cs.)
    //
    // PORT-NOTE: the 4th parameter is `object? vc` (a placeholder for the not-yet-ported
    // VerbosityContext). Callers pass null; this wave treats verbosity as absent (no hover expansion,
    // no MaxTruncationLength override).
    internal string TypeToStringEx(Type t, Node? enclosingDeclaration, TypeFormatFlags flags, object? vc)
    {
        if (serializationLevel >= MaxSerializationLevel)
        {
            return "?";
        }
        var newLine = "";
        if ((flags & TypeFormatFlags.MultilineObjectLiterals) != 0)
        {
            newLine = "\n";
        }
        var writer = PrinterNs.TextWriter.NewTextWriter(newLine, 0);
        // vc == null in this wave, so noTruncation reduces to the NoErrorTruncation / NoTruncation checks.
        var noTruncation = (compilerOptions.NoErrorTruncation == Tristate.True) || (flags & TypeFormatFlags.NoTruncation) != 0;

        // printer.go:202 — combinedFlags = toNodeBuilderFlags(flags) | FlagsIgnoreErrors (+ NoTruncation).
        var combinedFlags = (global::Ts.NodeBuilder.Flags)(flags & TypeFormatFlags.NodeBuilderFlagsMask)
            | global::Ts.NodeBuilder.Flags.IgnoreErrors;
        if (noTruncation)
        {
            combinedFlags |= global::Ts.NodeBuilder.Flags.NoTruncation;
        }

        // printer.go:206 — getNodeBuilder(). Request-scoped builder over a fresh emit context; the object /
        // reference / anonymous arms of typeToTypeNode need this builder's ctx (enclosingDeclaration mutation,
        // InObjectTypeLiteral flag, truncation/depth state). PORT-NOTE: the pooled getNodeBuilder cache is
        // deferred; a fresh builder per call is behaviorally identical (no cross-call state).
        var emitContext = CreateTypeToStringEmitContext();
        var nodeBuilder = NewNodeBuilder(emitContext);

        serializationLevel++;
        Node? typeNode = nodeBuilder.TypeToTypeNode(t, enclosingDeclaration, combinedFlags, global::Ts.NodeBuilder.InternalFlags.None, null /*tracker*/);
        serializationLevel--;
        if (typeNode == null)
        {
            throw new InvalidOperationException("should always get typenode");
        }

        // The unresolved type gets a synthesized comment on `any` to hint to users that it's not a plain
        // `any`; otherwise we always strip comments out.
        var printerOptions = new PrinterNs.PrinterOptions { RemoveComments = t != unresolvedType };
        var p = PrinterNs.Printer.NewPrinter(printerOptions, new PrinterNs.PrintHandlers(), nodeBuilder.EmitContext());
        SourceFile? sourceFile = null;
        if (enclosingDeclaration != null)
        {
            sourceFile = AstUtilities.GetSourceFileOfNode(enclosingDeclaration);
        }
        p.Write(typeNode, sourceFile, writer, null);
        var result = writer.String();

        var maxLength = DefaultMaximumTruncationLength * 2;
        if (noTruncation)
        {
            maxLength = NoTruncationMaximumTruncationLength * 2;
        }
        if (maxLength > 0 && result != "" && result.Length >= maxLength)
        {
            return result[0..(maxLength - "...".Length)] + "...";
        }
        return result;
    }

    // nodebuilderimpl.go:3176 — typeToTypeNode, exposed to the request-scoped NodeBuilder
    // (NodeBuilder/NodeBuilderImpl.cs). Both the error-message serializer (TypeToStringEx above) and the
    // .d.ts request builder share this one synthesis core; the request builder threads its own factory
    // (the emit-context factory) rather than the type-to-string emit context's.
    internal Node NodeBuilderTypeToTypeNode(Type t, global::Ts.Ast.NodeFactory f) => TypeToTypeNode(t, f);

    // nodebuilderimpl.go:3176 — typeToTypeNode (intrinsic / literal / union / intersection / named slice).
    // PORT-NOTE: the Go original threads a NodeBuilderContext (b.ctx) for expansion depth, truncation, and
    // encounteredError tracking. This breadth-first slice is context-free: it only needs the AST factory to
    // build the keyword/literal/composite type nodes the common error messages require.
    private Node TypeToTypeNode(Type t, global::Ts.Ast.NodeFactory f)
    {
        // nodebuilderimpl.go:3198 — reduce unless NoTypeReduction. (No FlagsNoTypeReduction plumbing yet;
        // reduction is safe and matches the default caller flags.)
        t = GetReducedType(t);

        if ((t.flags & TypeFlags.Any) != 0)
        {
            if (t == intrinsicMarkerType)
            {
                return f.NewKeywordTypeNode(SyntaxKind.IntrinsicKeyword);
            }
            return f.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }
        if ((t.flags & TypeFlags.Unknown) != 0)
        {
            return f.NewKeywordTypeNode(SyntaxKind.UnknownKeyword);
        }
        if ((t.flags & TypeFlags.String) != 0)
        {
            return f.NewKeywordTypeNode(SyntaxKind.StringKeyword);
        }
        if ((t.flags & TypeFlags.Number) != 0)
        {
            return f.NewKeywordTypeNode(SyntaxKind.NumberKeyword);
        }
        if ((t.flags & TypeFlags.BigInt) != 0)
        {
            return f.NewKeywordTypeNode(SyntaxKind.BigIntKeyword);
        }
        if ((t.flags & TypeFlags.Boolean) != 0 && t.alias == null)
        {
            return f.NewKeywordTypeNode(SyntaxKind.BooleanKeyword);
        }
        // nodebuilderimpl.go:3231 — enum-like (must precede the literal arms: enum literal types carry
        // StringLiteral/NumberLiteral flags but render as their enum names). PORT-NOTE (reduced): the
        // shouldExpandType union-expansion arm short-circuits (no VerbosityContext), and the import-type
        // member arm is owned by the symbol-chain wave; the qualified `E.member` arm is ported via a
        // direct qualified-name build (appendReferenceToType reduced to its TypeReferenceNode case).
        if ((t.flags & TypeFlags.EnumLike) != 0)
        {
            if ((t.symbol!.Flags & SymbolFlags.EnumMember) != 0)
            {
                var parentSymbol = GetParentOfSymbol(t.symbol)!;
                var parentName = SymbolToTypeReferenceNode(parentSymbol, f);
                if (GetDeclaredTypeOfSymbol(parentSymbol) == t)
                {
                    return parentName;
                }
                var memberName = AstUtilities.SymbolName(t.symbol);
                if (global::Ts.Scanner.Scanner.IsIdentifierText(memberName, LanguageVariant.Standard))
                {
                    return f.NewTypeReferenceNode(
                        f.NewQualifiedName(parentName.AsTypeReferenceNode().TypeName, f.NewIdentifier(memberName)),
                        null /*typeArguments*/);
                }
                // Non-identifier member names need the typeof-indexed-access form
                // (`(typeof E)["a b"]`, nodebuilderimpl.go:3243-3250).
                return f.NewIndexedAccessTypeNode(
                    f.NewTypeQueryNode(parentName.AsTypeReferenceNode().TypeName, null),
                    f.NewLiteralTypeNode(NewTypeToStringStringLiteral(memberName, f)));
            }
            return SymbolToTypeReferenceNode(t.symbol, f);
        }
        if ((t.flags & TypeFlags.StringLiteral) != 0)
        {
            var lit = NewTypeToStringStringLiteral((string)t.AsLiteralType().value!, f);
            return f.NewLiteralTypeNode(lit);
        }
        if ((t.flags & TypeFlags.NumberLiteral) != 0)
        {
            var value = (global::Ts.JsNum.Number)t.AsLiteralType().value!;
            var s = value.ToString();
            if (s.Length > 0 && s[0] == '-')
            {
                return f.NewLiteralTypeNode(f.NewPrefixUnaryExpression(SyntaxKind.MinusToken, f.NewNumericLiteral(s[1..], TokenFlags.None)));
            }
            return f.NewLiteralTypeNode(f.NewNumericLiteral(s, TokenFlags.None));
        }
        if ((t.flags & TypeFlags.BigIntLiteral) != 0)
        {
            // PORT-NOTE: pseudoBigIntToString slice lives in the deferred NodeBuilderImpl port; fall back
            // to the literal type's own string form (matches the value the evaluator stored).
            return f.NewLiteralTypeNode(f.NewBigIntLiteral(t.AsLiteralType().value?.ToString() + "n", TokenFlags.None));
        }
        if ((t.flags & TypeFlags.BooleanLiteral) != 0)
        {
            if ((bool)t.AsLiteralType().value!)
            {
                return f.NewLiteralTypeNode(f.NewKeywordExpression(SyntaxKind.TrueKeyword));
            }
            return f.NewLiteralTypeNode(f.NewKeywordExpression(SyntaxKind.FalseKeyword));
        }
        if ((t.flags & TypeFlags.Void) != 0)
        {
            return f.NewKeywordTypeNode(SyntaxKind.VoidKeyword);
        }
        if ((t.flags & TypeFlags.Undefined) != 0)
        {
            return f.NewKeywordTypeNode(SyntaxKind.UndefinedKeyword);
        }
        if ((t.flags & TypeFlags.Null) != 0)
        {
            return f.NewLiteralTypeNode(f.NewKeywordExpression(SyntaxKind.NullKeyword));
        }
        if ((t.flags & TypeFlags.Never) != 0)
        {
            return f.NewKeywordTypeNode(SyntaxKind.NeverKeyword);
        }
        if ((t.flags & TypeFlags.ESSymbol) != 0)
        {
            return f.NewKeywordTypeNode(SyntaxKind.SymbolKeyword);
        }
        if ((t.flags & TypeFlags.NonPrimitive) != 0)
        {
            return f.NewKeywordTypeNode(SyntaxKind.ObjectKeyword);
        }

        // nodebuilderimpl.go:3409 — union origin unwrap + union/intersection constituent mapping.
        if ((t.flags & TypeFlags.Union) != 0 && t.AsUnionType().origin != null)
        {
            t = t.AsUnionType().origin!;
        }
        if ((t.flags & (TypeFlags.Union | TypeFlags.Intersection)) != 0)
        {
            Type[] types;
            if ((t.flags & TypeFlags.Union) != 0)
            {
                types = FormatUnionTypes(t.AsUnionType().types, false /*expandingEnum*/);
            }
            else
            {
                types = t.AsIntersectionType().types;
            }
            if (types.Length == 1)
            {
                return TypeToTypeNode(types[0], f);
            }
            var typeNodes = MapToTypeNodes(types, f);
            if (typeNodes != null && typeNodes.Nodes.Length > 0)
            {
                if ((t.flags & TypeFlags.Union) != 0)
                {
                    return f.NewUnionTypeNode(typeNodes);
                }
                return f.NewIntersectionTypeNode(typeNodes);
            }
            // Empty union/intersection with no constituents renders as `never`/`unknown` upstream via the
            // deferred error path; for a stringified diagnostic we emit the neutral fallback.
            return TypeToTypeNodeFallback(f);
        }

        // nodebuilderimpl.go:3232 / 3367 / 3398 — enum-like and named types (type parameters, class /
        // interface, references) reduce to a symbol-name type reference in the common error case.
        if (t.symbol != null && ((t.flags & TypeFlags.EnumLike) != 0
            || (t.flags & TypeFlags.TypeParameter) != 0
            || (t.objectFlags & (ObjectFlags.Reference | ObjectFlags.ClassOrInterface)) != 0))
        {
            return SymbolToTypeReferenceNode(t.symbol, f);
        }

        // All remaining kinds (conditional / mapped / indexed access / template literal / anonymous
        // object / substitution / index / this-type / reference type-argument expansion) are part of the
        // deferred NodeBuilderImpl port.
        return TypeToTypeNodeFallback(f);
    }

    // printer.go:383 — formatUnionTypes.
    private Type[] FormatUnionTypes(Type[] types, bool expandingEnum)
    {
        var result = new System.Collections.Generic.List<Type>();
        TypeFlags flags = 0;
        for (var i = 0; i < types.Length; i++)
        {
            var t = types[i];
            flags |= t.flags;
            if ((t.flags & TypeFlags.Nullable) == 0)
            {
                if ((t.flags & TypeFlags.BooleanLiteral) != 0 || (!expandingEnum && (t.flags & TypeFlags.EnumLike) != 0))
                {
                    Type baseType;
                    if ((t.flags & TypeFlags.BooleanLiteral) != 0)
                    {
                        baseType = booleanType!;
                    }
                    else
                    {
                        baseType = GetBaseTypeOfEnumLikeType(t);
                    }
                    if ((baseType.flags & TypeFlags.Union) != 0)
                    {
                        var baseTypes = baseType.AsUnionType().types;
                        var count = baseTypes.Length;
                        if (i + count <= types.Length && GetRegularTypeOfLiteralType(types[i + count - 1]) == GetRegularTypeOfLiteralType(baseTypes[count - 1]))
                        {
                            result.Add(baseType);
                            i += count - 1;
                            continue;
                        }
                    }
                }
                result.Add(t);
            }
        }
        if ((flags & TypeFlags.Null) != 0)
        {
            result.Add(nullType!);
        }
        if ((flags & TypeFlags.Undefined) != 0)
        {
            result.Add(undefinedType!);
        }
        return result.ToArray();
    }

    // nodebuilderimpl.go:337 — mapToTypeNodes (bare-list slice, no truncation/name-collision handling).
    // PORT-NOTE: truncation elision ("... N more ...") and the seenNames collision de-dup are part of the
    // deferred NodeBuilderImpl port.
    private TypeList? MapToTypeNodes(Type[] list, global::Ts.Ast.NodeFactory f)
    {
        if (list.Length == 0)
        {
            return null;
        }
        var result = new Node[list.Length];
        for (var i = 0; i < list.Length; i++)
        {
            result[i] = TypeToTypeNode(list[i], f);
        }
        return f.NewNodeList(result);
    }

    // nodebuilderimpl.go:638 — symbolToTypeNode, reduced to a bare identifier type reference.
    // PORT-NOTE: the full symbol chain (qualified names, import types, type-argument nodes, accessibility
    // tracking) lands with the deferred NodeBuilderImpl port; the common error message only needs the
    // symbol's own name.
    private Node SymbolToTypeReferenceNode(Symbol symbol, global::Ts.Ast.NodeFactory f)
    {
        var name = AstUtilities.SymbolName(symbol);
        return f.NewTypeReferenceNode(f.NewIdentifier(name), null /*typeArguments*/);
    }

    // nodebuilderimpl.go:3495 — newStringLiteral (double-quote form; NeverAsciiEscape handled by the
    // NoAsciiEscaping emit flag in the full port — omitted here, the Printer escapes deterministically).
    private static Node NewTypeToStringStringLiteral(string text, global::Ts.Ast.NodeFactory f)
    {
        return f.NewStringLiteral(text, TokenFlags.None);
    }

    // Neutral fallback for the not-yet-ported type kinds: the `any` keyword. PORT-NOTE: replace each
    // fall-through with its faithful nodebuilderimpl.go branch as the full NodeBuilder port lands.
    private static Node TypeToTypeNodeFallback(global::Ts.Ast.NodeFactory f)
    {
        return f.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
    }
}
