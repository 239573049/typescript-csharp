// Additive public re-exports consumed by the Ts.LanguageService api-encoder port
// (internal/api/session.go + internal/api/encoder → Api/**).
//
// DEVIATION-REGISTER (AGENTS.md §6, logged in the port report): the dispatcher scoped
// this session to Api/Encoder/** + this file + Api seam call sites, but the api handlers
// call checker-package exported (capitalized) API — exports.go / services.go /
// checker.go — whose C# cores landed only as internal members (or had no C# port yet).
// Passthrough members are 1:1 delegations to the existing internal implementation with
// no logic change. GetTrueTypeOfConditionalType / GetFalseTypeOfConditionalType /
// GetRestTypeOfSignature are 1:1 ports of private checker.go bodies (checker.go:9607,
// checker.go:24446-24461) that had no C# port; GetReferencesToSymbolInFile + its three
// private helpers are a 1:1 port of services.go:576-687 for the same reason. This follows
// the established convention of Checker.LsExports.cs / Checker.FindRefs.LsExports.cs
// (and commits 9109b7cdf, 4c1c94bc8). Members keep their Go exports.go names; the
// extension-method shape preserves the Go call form at the call site. GetImmediateAliasedSymbol
// already exists as GetImmediateAliasedSymbolEx in Checker.FindRefs.LsExports.cs — it is
// consumed from there (a second extension of the same name would be ambiguous).

using System;
using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Checker;

// ── partial Checker additions (members whose core is private or not yet ported) ─────────────
public sealed partial class Checker
{
    // checker.go:9607-9609 — getRestTypeOfSignature:
    //   func (c *Checker) getRestTypeOfSignature(signature *Signature) *Type {
    //       return core.OrElse(c.tryGetRestTypeOfSignature(signature), c.anyType)
    //   }
    public global::Ts.Checker.Type GetRestTypeOfSignature(Signature signature)
    {
        return global::Ts.Core.CoreUtil.OrElse(TryGetRestTypeOfSignature(signature)!, anyType!);
    }

    // checker.go:24446-24453 — getTrueTypeFromConditionalType.
    public global::Ts.Checker.Type GetTrueTypeOfConditionalType(global::Ts.Checker.Type t)
    {
        ConditionalType d = t.AsConditionalType();
        if (d.resolvedTrueType == null)
        {
            d.resolvedTrueType = InstantiateType(GetTypeFromTypeNode(d.root.node!.TrueType!), d.mapper);
        }
        return d.resolvedTrueType;
    }

    // checker.go:24455-24461 — getFalseTypeFromConditionalType.
    public global::Ts.Checker.Type GetFalseTypeOfConditionalType(global::Ts.Checker.Type t)
    {
        ConditionalType d = t.AsConditionalType();
        if (d.resolvedFalseType == null)
        {
            d.resolvedFalseType = InstantiateType(GetTypeFromTypeNode(d.root.node!.FalseType!), d.mapper);
        }
        return d.resolvedFalseType;
    }

    // printer.go:423-427 — SignatureToSignatureDeclaration:
    //   func (c *Checker) SignatureToSignatureDeclaration(signature *Signature, kind ast.Kind,
    //       enclosingDeclaration *ast.Node, flags nodebuilder.Flags) *ast.Node {
    //       nodeBuilder, release := c.getNodeBuilder()
    //       defer release()
    //       return nodeBuilder.SignatureToSignatureDeclaration(signature, kind,
    //           enclosingDeclaration, flags, nodebuilder.InternalFlagsNone, nil)
    //   }
    // Go getNodeBuilder caches c.typeToStringNodebuilder (nodebuilder.go:288-297,
    // getNodeBuilderEx(nil)); the pooled builder cache is deferred in the C# port
    // (Checker.TypeToString.cs PORT-NOTE), so a fresh request-scoped builder over a new
    // emit context with idToSymbol=nil is the established substitute — same shape as the
    // TypeToTypeNode body in Checker.Services.cs.
    public global::Ts.Ast.Node? SignatureToSignatureDeclaration(Signature signature, global::Ts.Ast.SyntaxKind kind, global::Ts.Ast.Node? enclosingDeclaration, global::Ts.NodeBuilder.Flags flags)
    {
        var nodeBuilder = new NodeBuilder(this, CreateTypeToStringEmitContext(), null /*idToSymbol*/);
        return nodeBuilder.SignatureToSignatureDeclaration(signature, kind, enclosingDeclaration, flags, global::Ts.NodeBuilder.InternalFlags.None, null /*tracker*/);
    }

    // services.go:576-611 — GetReferencesToSymbolInFile: identifiers in the file that
    // reference the given symbol.
    public global::Ts.Ast.Node[] GetReferencesToSymbolInFile(global::Ts.Ast.SourceFile sourceFile, global::Ts.Ast.Symbol symbol)
    {
        string identifierText = symbol.Name;
        var result = new List<global::Ts.Ast.Node>();
        foreach (global::Ts.Ast.Node token in GetPossibleSymbolReferenceNodes(sourceFile, identifierText, sourceFile.AsNode()))
        {
            if (!global::Ts.Ast.NodeTests.IsIdentifier(token))
            {
                continue;
            }
            global::Ts.Ast.Identifier id = token.AsIdentifier();
            if (id.Text != identifierText)
            {
                continue;
            }
            global::Ts.Ast.Symbol? refSymbol = GetSymbolAtLocation(token);
            if (refSymbol == symbol)
            {
                result.Add(token);
                continue;
            }
            if (token.Parent != null && token.Parent.Kind == global::Ts.Ast.SyntaxKind.ShorthandPropertyAssignment)
            {
                global::Ts.Ast.Symbol? shorthandSymbol = GetShorthandAssignmentValueSymbolCore(token.Parent);
                if (shorthandSymbol == symbol)
                {
                    result.Add(token);
                    continue;
                }
            }
            if (token.Parent != null && global::Ts.Ast.NodeTests.IsExportSpecifier(token.Parent))
            {
                global::Ts.Ast.Symbol? localSymbol = GetLocalSymbolForExportSpecifier(token.AsIdentifier(), refSymbol, token.Parent.AsExportSpecifier());
                if (localSymbol == symbol)
                {
                    result.Add(token);
                    continue;
                }
            }
        }
        return result.ToArray();
    }

    // services.go:613-620 — getLocalSymbolForExportSpecifier.
    private global::Ts.Ast.Symbol? GetLocalSymbolForExportSpecifier(global::Ts.Ast.Identifier referenceLocation, global::Ts.Ast.Symbol? referenceSymbol, global::Ts.Ast.ExportSpecifier exportSpecifier)
    {
        if (IsExportSpecifierAlias(referenceLocation, exportSpecifier))
        {
            global::Ts.Ast.Symbol? symbol = GetExportSpecifierLocalTargetSymbolCore(exportSpecifier.AsNode());
            if (symbol != null)
            {
                return symbol;
            }
        }
        return referenceSymbol;
    }

    // services.go:622-633 — isExportSpecifierAlias.
    private static bool IsExportSpecifierAlias(global::Ts.Ast.Identifier referenceLocation, global::Ts.Ast.ExportSpecifier exportSpecifier)
    {
        global::Ts.Core.Debug.Assert(exportSpecifier.PropertyName == referenceLocation || exportSpecifier.Name() == referenceLocation, "referenceLocation is not export specifier name or property name");
        global::Ts.Ast.Node? propertyName = exportSpecifier.PropertyName;
        if (propertyName != null)
        {
            // Given `export { foo as bar } [from "someModule"]`: It's an alias at `foo`, but at `bar` it's a new symbol.
            return propertyName == referenceLocation;
        }
        else
        {
            // `export { foo } from "foo"` is a re-export.
            // `export { foo };` is not a re-export, it creates an alias for the local variable `foo`.
            return exportSpecifier.Parent!.Parent!.ModuleSpecifier() == null;
        }
    }

    // services.go:635-642 — getPossibleSymbolReferenceNodes.
    private static global::Ts.Ast.Node[] GetPossibleSymbolReferenceNodes(global::Ts.Ast.SourceFile sourceFile, string symbolName, global::Ts.Ast.Node container)
    {
        // core.MapNonNil infers U = Node? (the nil branch); the filter guarantees non-nil,
        // so the result is re-cast to Node[] (Go's []*ast.Node has no such annotation).
        global::Ts.Ast.Node?[] mapped = global::Ts.Core.CoreUtil.MapNonNil(
            GetPossibleSymbolReferencePositions(sourceFile, symbolName, container),
            pos =>
            {
                global::Ts.Ast.Node? referenceLocation = global::Ts.AstNav.Tokens.GetTouchingPropertyName(sourceFile, pos);
                if (referenceLocation != sourceFile.AsNode())
                {
                    return referenceLocation;
                }
                return null;
            }) ?? [];
        var result = new global::Ts.Ast.Node[mapped.Length];
        int n = 0;
        foreach (global::Ts.Ast.Node? node in mapped)
        {
            if (node != null)
            {
                result[n++] = node;
            }
        }
        return result[..n];
    }

    // services.go:644-687 — getPossibleSymbolReferencePositions.
    private static int[] GetPossibleSymbolReferencePositions(global::Ts.Ast.SourceFile sourceFile, string symbolName, global::Ts.Ast.Node? container)
    {
        var positions = new List<int>();

        // TODO: Cache symbol existence for files to save text search
        // Also, need to make this work for unicode escapes.

        // Be resilient in the face of a symbol with no name or zero length name
        if (symbolName == "")
        {
            return positions.ToArray();
        }

        string text = sourceFile.Text();
        int sourceLength = text.Length;
        int symbolNameLength = symbolName.Length;

        container ??= sourceFile.AsNode();

        int position = text.IndexOf(symbolName, container.Pos, StringComparison.Ordinal);
        int endPos = container.End;
        while (position >= 0 && position < endPos)
        {
            // We found a match.  Make sure it's not part of a larger word (i.e. the char
            // before and after it have to be a non-identifier char).
            int endPosition = position + symbolNameLength;

            if ((position == 0 || !global::Ts.Scanner.Scanner.IsIdentifierPart(text[position - 1])) &&
                (endPosition == sourceLength || !global::Ts.Scanner.Scanner.IsIdentifierPart(text[endPosition])))
            {
                // Found a real match.  Keep searching.
                positions.Add(position);
            }
            int startIndex = position + symbolNameLength + 1;
            if (startIndex > text.Length)
            {
                break;
            }
            int foundIndex = text.IndexOf(symbolName, startIndex, StringComparison.Ordinal);
            if (foundIndex != -1)
            {
                position = foundIndex;
            }
            else
            {
                break;
            }
        }

        return positions.ToArray();
    }
}

// ── instance-API re-exports (extension form; internals keep their names) ────────────────────
public static class CheckerApiExports
{
    // exports.go:33 — GetAnyType.
    public static global::Ts.Checker.Type? GetAnyType(this Checker c) => c.anyType;

    // exports.go:49 — GetBigIntType.
    public static global::Ts.Checker.Type? GetBigIntType(this Checker c) => c.bigintType;

    // exports.go:17 — GetBooleanType.
    public static global::Ts.Checker.Type? GetBooleanType(this Checker c) => c.booleanType;

    // exports.go:21 — GetVoidType.
    public static global::Ts.Checker.Type? GetVoidType(this Checker c) => c.voidType;

    // exports.go:41 — GetNeverType.
    public static global::Ts.Checker.Type? GetNeverType(this Checker c) => c.neverType;

    // exports.go:45 — GetUnknownType.
    public static global::Ts.Checker.Type? GetUnknownType(this Checker c) => c.unknownType;

    // exports.go:53 — GetESSymbolType.
    public static global::Ts.Checker.Type? GetESSymbolType(this Checker c) => c.esSymbolType;

    // exports.go:29 — GetNullType.
    public static global::Ts.Checker.Type? GetNullType(this Checker c) => c.nullType;

    // exports.go:61 — GetUnknownSymbol.
    public static global::Ts.Ast.Symbol GetUnknownSymbol(this Checker c) => c.unknownSymbol;

    // exports.go:65 — GetUndefinedSymbol.
    public static global::Ts.Ast.Symbol GetUndefinedSymbol(this Checker c) => c.undefinedSymbol;

    // exports.go:69 — GetArgumentsSymbol.
    public static global::Ts.Ast.Symbol GetArgumentsSymbol(this Checker c) => c.argumentsSymbol;

    // exports.go:73 — GetUnknownSignature.
    public static global::Ts.Checker.Signature? GetUnknownSignature(this Checker c) => c.unknownSignature;

    // exports.go:57 — GetBaseTypeOfLiteralType.
    public static global::Ts.Checker.Type GetBaseTypeOfLiteralType(this Checker c, global::Ts.Checker.Type t) =>
        c.GetBaseTypeOfLiteralType(t);

    // exports.go:216 — IsArrayType.
    public static bool IsArrayType(this Checker c, global::Ts.Checker.Type t) => c.IsArrayType(t);

    // exports.go:313 — IsContextSensitive.
    public static bool IsContextSensitive(this Checker c, global::Ts.Ast.Node node) => c.IsContextSensitive(node);

    // exports.go:309 — GetIndexInfosOfType.
    public static global::Ts.Checker.IndexInfo[] GetIndexInfosOfType(this Checker c, global::Ts.Checker.Type t) =>
        c.GetIndexInfosOfType(t);

    // exports.go:357 — CompareSymbols (checker.compareSymbols; C# core is
    // CompareSymbolsWorker in Checker.TypeOps.Support.cs).
    public static int CompareSymbols(this Checker c, global::Ts.Ast.Symbol? s1, global::Ts.Ast.Symbol? s2) =>
        c.CompareSymbolsWorker(s1, s2);
}
