// Ported from tsgo internal/ls/findallreferences.go (identifier-reference foundation subset).
//
// Scope note (breadth-first wave): the faithful Go pipeline is
//   ProvideReferences (:694) → getReferencedSymbolsForNode (:1161) → per-symbol search
//   state with checker.GetSymbolAtLocation comparison and alias chains.
// checker.GetSymbolAtLocation is still internal to Ts.Compiler *and* a throwing stub,
// so this foundation subset keeps the same shape — position → target → per-file
// candidate scan (getPossibleSymbolReferencePositions) → symbol comparison — but the
// comparison channel is the public emit-resolver surface: two identifier use-sites
// reference the same symbol exactly when they resolve to the same value declaration.
// Deferred (each marked at its branch below):
//   * special families (getReferencedSymbolsSpecial: labels, this/super, keywords),
//   * string/numeric-literal property references,
//   * import/export alias deep chains (skipPastExportOrImportSpecifierOrUnion,
//     getMergedAliasedSymbolOfNamespaceExportDeclaration).

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods

namespace Ts.Ls;

// findallreferences.go: ReferenceEntry, reduced to the location payload the raw
// (non-LSP) foundation API returns; the LSP converters live in the deferred layer.
public sealed record ReferenceLocation(string FileName, global::Ts.Core.TextRange TextSpan);

public sealed partial class LanguageService
{
    // findallreferences.go:694 ProvideReferences / :1094 GetReferencedSymbolsForNode
    // (foundation subset; includes declarations, matching the Go default of
    // includeDeclaration=true requests).
    public ReferenceLocation[] GetReferencesAtPosition(
        string fileName,
        int position,
        CancellationToken cancellationToken = default)
    {
        var node = GetNodeAtPosition(fileName, position, out var file);
        if (node.Kind == global::Ts.Ast.SyntaxKind.SourceFile)
        {
            // Go resolves module references for a source-file position
            // (getReferencedSymbolsForModuleIfDeclaredBySourceFile). Deferred with the
            // module/alias chain work.
            return [];
        }

        if (node.Kind != global::Ts.Ast.SyntaxKind.Identifier &&
            node.Kind != global::Ts.Ast.SyntaxKind.PrivateIdentifier)
        {
            // Go dispatches special reference families first (getReferencedSymbolsSpecial:
            // labels, this/super keywords, type keywords, string literals). Deferred;
            // the foundation subset serves identifier-driven value references only.
            return [];
        }

        var (checker, done) = host.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            var emitResolver = checker.GetEmitResolver();
            var targetDeclaration = GetReferencedDeclaration(emitResolver, node);
            if (targetDeclaration is null)
            {
                return [];
            }

            var searchName = node.Text();
            var locations = new List<ReferenceLocation>();
            // Go iterates the request's sourceFiles slice in program order; positions
            // within a file ascend, so the result order is deterministic.
            foreach (var sourceFile in program.SourceFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var reference in GetPossibleSymbolReferenceNodes(sourceFile, searchName, null))
                {
                    if (!IsValidReferencePosition(reference, searchName))
                    {
                        continue;
                    }
                    var declaration = GetReferencedDeclaration(emitResolver, reference);
                    if (!ReferenceEquals(declaration, targetDeclaration))
                    {
                        continue;
                    }
                    locations.Add(new ReferenceLocation(
                        sourceFile.FileName(),
                        GetRangeOfNode(reference, sourceFile, null)));
                }
            }
            return locations.ToArray();
        }
        finally
        {
            done();
        }
    }

    // Symbol-identity channel for the comparison above. Faithful Go compares
    // *ast.Symbol identities from checker.GetSymbolAtLocation after alias skipping;
    // until that surface is public this resolves each identifier to its symbol's
    // value declaration through the public emit resolver. GetReferencedValueDeclaration
    // already folds export-value symbols onto their export symbol
    // (GetExportSymbolOfValueSymbolIfExported), which is the alias alignment available
    // on the public surface; deeper import/export specifier chains are deferred.
    private static global::Ts.Ast.Node? GetReferencedDeclaration(
        global::Ts.Checker.EmitResolver emitResolver,
        global::Ts.Ast.Node node)
    {
        if (IsMemberNamePosition(node))
        {
            // Member names are not lexically resolvable — resolveName would bind them
            // to an unrelated outer symbol of the same text. The member channel only
            // answers from already-resolved checker links, so unresolved member
            // references drop out (deferred with checker.GetSymbolAtLocation).
            return emitResolver.GetReferencedMemberValueDeclaration(node);
        }
        return emitResolver.GetReferencedValueDeclaration(node);
    }

    // Positions in which an identifier names a member rather than a lexical value.
    private static bool IsMemberNamePosition(global::Ts.Ast.Node node)
    {
        var parent = node.Parent;
        if (parent is null)
        {
            return false;
        }
        switch (parent.Kind)
        {
            case global::Ts.Ast.SyntaxKind.PropertyAccessExpression:
                return parent.Name() == node;
            case global::Ts.Ast.SyntaxKind.QualifiedName:
                return parent.AsQualifiedName().Right == node;
            case global::Ts.Ast.SyntaxKind.PropertyAssignment:
            case global::Ts.Ast.SyntaxKind.PropertyDeclaration:
            case global::Ts.Ast.SyntaxKind.PropertySignature:
            case global::Ts.Ast.SyntaxKind.MethodDeclaration:
            case global::Ts.Ast.SyntaxKind.MethodSignature:
            case global::Ts.Ast.SyntaxKind.GetAccessor:
            case global::Ts.Ast.SyntaxKind.SetAccessor:
            case global::Ts.Ast.SyntaxKind.EnumMember:
                return parent.Name() == node;
            default:
                return false;
        }
    }

    // findallreferences.go:1529 getPossibleSymbolReferenceNodes
    private static List<global::Ts.Ast.Node> GetPossibleSymbolReferenceNodes(
        global::Ts.Ast.SourceFile sourceFile,
        string symbolName,
        global::Ts.Ast.Node? container)
    {
        var nodes = new List<global::Ts.Ast.Node>();
        foreach (var pos in GetPossibleSymbolReferencePositions(sourceFile, symbolName, container))
        {
            var referenceLocation = global::Ts.AstNav.Tokens.GetTouchingPropertyName(sourceFile, pos);
            if (referenceLocation.Kind != global::Ts.Ast.SyntaxKind.SourceFile)
            {
                nodes.Add(referenceLocation);
            }
        }
        return nodes;
    }

    // findallreferences.go:1538 getPossibleSymbolReferencePositions
    private static List<int> GetPossibleSymbolReferencePositions(
        global::Ts.Ast.SourceFile sourceFile,
        string symbolName,
        global::Ts.Ast.Node? container)
    {
        var positions = new List<int>();

        // TODO: Cache symbol existence for files to save text search
        // Also, need to make this work for unicode escapes.

        // Be resilient in the face of a symbol with no name or zero length name
        if (symbolName.Length == 0)
        {
            return positions;
        }

        var text = sourceFile.Text();
        var sourceLength = text.Length;
        var symbolNameLength = symbolName.Length;

        // Go: container == nil → sourceFile.AsNode(). All offsets here are UTF-16 code
        // units (the repository position currency); Go's byte offsets coincide only for
        // ASCII, and its first strings.Index is container-relative — which equals the
        // absolute position solely because the container is the file (Pos 0) on every
        // current path. The absolute IndexOf overload keeps the loop in one currency.
        var containerPos = container?.Pos ?? 0;
        var endPos = container?.End ?? sourceFile.End;

        var position = text.IndexOf(symbolName, containerPos, StringComparison.Ordinal);
        while (position >= 0 && position < endPos)
        {
            // We found a match.  Make sure it's not part of a larger word (i.e. the char
            // before and after it have to be a non-identifier char).
            var endPosition = position + symbolNameLength;

            if ((position == 0 || !global::Ts.Scanner.Scanner.IsIdentifierPart(text[position - 1])) &&
                (endPosition == sourceLength || !global::Ts.Scanner.Scanner.IsIdentifierPart(text[endPosition])))
            {
                // Found a real match.  Keep searching.
                positions.Add(position);
            }
            var startIndex = position + symbolNameLength + 1;
            if (startIndex > text.Length)
            {
                break;
            }
            position = text.IndexOf(symbolName, startIndex, StringComparison.Ordinal);
        }

        return positions;
    }

    // findallreferences.go:348 isValidReferencePosition
    private static bool IsValidReferencePosition(global::Ts.Ast.Node node, string searchSymbolName)
    {
        switch (node.Kind)
        {
            case global::Ts.Ast.SyntaxKind.PrivateIdentifier:
                // !!!
                // if (isJSDocMemberName(node.Parent)) {
                // 	return true;
                // }
                return node.Text().Length == searchSymbolName.Length;
            case global::Ts.Ast.SyntaxKind.Identifier:
                return node.Text().Length == searchSymbolName.Length;
            case global::Ts.Ast.SyntaxKind.NoSubstitutionTemplateLiteral:
            case global::Ts.Ast.SyntaxKind.StringLiteral:
            case global::Ts.Ast.SyntaxKind.NumericLiteral:
                // Go admits literal property names / module names / external-module
                // import-equals expressions here (isLiteralNameOfPropertyDeclarationOrIndexAccess
                // and friends). That is the string-property deep path — deferred with the
                // member-reference work.
                return false;
            case global::Ts.Ast.SyntaxKind.DefaultKeyword:
                return "default".Length == searchSymbolName.Length;
        }
        return false;
    }

    // findallreferences.go:329 getRangeOfNode
    private static global::Ts.Core.TextRange GetRangeOfNode(
        global::Ts.Ast.Node node,
        global::Ts.Ast.SourceFile? sourceFile,
        global::Ts.Ast.Node? endNode)
    {
        sourceFile ??= global::Ts.Ast.AstUtilities.GetSourceFileOfNode(node)!;
        var start = global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, sourceFile, false /*includeJsDoc*/);
        var end = (endNode ?? node).End;
        if (global::Ts.Ast.AstUtilities.IsStringLiteralLike(node) && (end - start) > 2)
        {
            if (endNode is not null)
            {
                // Go panic; InvalidOperationException is the repository's panic mapping.
                throw new InvalidOperationException("endNode is not nil for stringLiteralLike");
            }
            start += 1;
            end -= 1;
        }
        if (endNode is not null && endNode.Kind == global::Ts.Ast.SyntaxKind.CaseBlock)
        {
            end = endNode.Pos;
        }
        return new global::Ts.Core.TextRange(start, end);
    }
}
