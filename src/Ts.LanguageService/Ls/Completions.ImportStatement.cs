// Ported from tsgo internal/ls/completions.go — the import-statement completion cluster
// (getImportStatementCompletionInfo and its helpers, completions.go:5135-5315).

using System;
using System.Linq;
using Ts.Ast;

namespace Ts.Ls;

// completions.go:127-134 — importStatementCompletionInfo.
internal sealed class ImportStatementCompletionInfo
{
    internal bool isKeywordOnlyCompletion;
    internal SyntaxKind keywordCompletion; // TokenKind
    internal bool isNewIdentifierLocation;
    internal bool isTopLevelTypeOnly;
    internal bool couldBeTypeOnlyImportSpecifier;
    internal global::Ts.LsProto.Range? replacementSpan;
}

public sealed partial class LanguageService
{
    // completions.go:5135-5210 — getImportStatementCompletionInfo.
    internal ImportStatementCompletionInfo GetImportStatementCompletionInfo(Node contextToken, SourceFile sourceFile)
    {
        var result = new ImportStatementCompletionInfo();
        Node? candidate = null;
        var parent = contextToken.Parent!;
        if (NodeTests.IsImportEqualsDeclaration(parent))
        {
            // import Foo |
            // import Foo f|
            var lastToken = LsDeps.GetLastToken(parent, sourceFile);
            if (contextToken.Kind == SyntaxKind.Identifier && lastToken != contextToken)
            {
                result.keywordCompletion = SyntaxKind.FromKeyword;
                result.isKeywordOnlyCompletion = true;
            }
            else
            {
                if (contextToken.Kind != SyntaxKind.TypeKeyword)
                {
                    result.keywordCompletion = SyntaxKind.TypeKeyword;
                }
                if (IsModuleSpecifierMissingOrEmpty(parent.AsImportEqualsDeclaration().ModuleReference))
                {
                    candidate = parent;
                }
            }
        }
        else if (CouldBeTypeOnlyImportSpecifier(parent, contextToken) && CanCompleteFromNamedBindings(parent.Parent!))
        {
            candidate = parent;
        }
        else if (NodeTests.IsNamedImports(parent) || NodeTests.IsNamespaceImport(parent))
        {
            if (!parent.Parent!.IsTypeOnly() && (contextToken.Kind == SyntaxKind.OpenBraceToken ||
                contextToken.Kind == SyntaxKind.ImportKeyword ||
                contextToken.Kind == SyntaxKind.CommaToken))
            {
                result.keywordCompletion = SyntaxKind.TypeKeyword;
            }
            if (CanCompleteFromNamedBindings(parent))
            {
                // At `import { ... } |` or `import * as Foo |`, the only possible completion is `from`
                if (contextToken.Kind == SyntaxKind.CloseBraceToken || contextToken.Kind == SyntaxKind.Identifier)
                {
                    result.isKeywordOnlyCompletion = true;
                    result.keywordCompletion = SyntaxKind.FromKeyword;
                }
                else
                {
                    candidate = parent.Parent!.Parent;
                }
            }
        }
        else if ((NodeTests.IsExportDeclaration(parent) && contextToken.Kind == SyntaxKind.AsteriskToken) ||
            (NodeTests.IsNamedExports(parent) && contextToken.Kind == SyntaxKind.CloseBraceToken))
        {
            result.isKeywordOnlyCompletion = true;
            result.keywordCompletion = SyntaxKind.FromKeyword;
        }
        else if (contextToken.Kind == SyntaxKind.ImportKeyword)
        {
            if (NodeTests.IsSourceFile(parent))
            {
                // A lone import keyword with nothing following it does not parse as a statement at all
                result.keywordCompletion = SyntaxKind.TypeKeyword;
                candidate = contextToken;
            }
            else if (NodeTests.IsImportDeclaration(parent))
            {
                // `import s| from`
                result.keywordCompletion = SyntaxKind.TypeKeyword;
                if (IsModuleSpecifierMissingOrEmpty(parent.ModuleSpecifier()!))
                {
                    candidate = parent;
                }
            }
        }

        if (candidate != null)
        {
            result.isNewIdentifierLocation = true;
            result.replacementSpan = GetSingleLineReplacementSpanForImportCompletionNode(candidate);
            result.couldBeTypeOnlyImportSpecifier = CouldBeTypeOnlyImportSpecifier(candidate, contextToken);
            if (NodeTests.IsImportDeclaration(candidate))
            {
                if (candidate.ImportClause() is { } importClause)
                {
                    result.isTopLevelTypeOnly = importClause.IsTypeOnly();
                }
            }
            else if (candidate.Kind == SyntaxKind.ImportEqualsDeclaration)
            {
                result.isTopLevelTypeOnly = candidate.IsTypeOnly();
            }
        }
        else
        {
            result.isNewIdentifierLocation = result.keywordCompletion == SyntaxKind.TypeKeyword;
        }
        return result;
    }

    // completions.go:5212-5258 — getSingleLineReplacementSpanForImportCompletionNode.
    internal global::Ts.LsProto.Range? GetSingleLineReplacementSpanForImportCompletionNode(Node? node)
    {
        if (node == null)
        {
            return null;
        }
        // node is ImportDeclaration | ImportEqualsDeclaration | ImportSpecifier | JSDocImportTag | Token<SyntaxKind.ImportKeyword>
        if (AstUtilities.FindAncestor(node, n => NodeTests.IsImportDeclaration(n) || NodeTests.IsImportEqualsDeclaration(n) || NodeTests.IsJSDocImportTag(n)) is { } ancestor)
        {
            node = ancestor;
        }
        var sourceFile = AstUtilities.GetSourceFileOfNode(node)!;
        // Use token position (excluding JSDoc/trivia) instead of node.Pos() to avoid including JSDoc comments
        var tokenPos = global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, sourceFile, false /*includeJSDoc*/);
        if (global::Ts.Printer.PrinterUtilities.GetLinesBetweenPositions(sourceFile, tokenPos, node.End) == 0)
        {
            return CreateLspRangeFromNode(node, sourceFile);
        }

        if (node.Kind == SyntaxKind.ImportKeyword || node.Kind == SyntaxKind.ImportSpecifier)
        {
            throw new InvalidOperationException("ImportKeyword was necessarily on one line; ImportSpecifier was necessarily parented in an ImportDeclaration");
        }

        // Guess which point in the import might actually be a later statement parsed as part of the import
        // during parser recovery - either in the middle of named imports, or the module specifier.
        Node? potentialSplitPoint;
        if (node.Kind == SyntaxKind.ImportDeclaration || node.Kind == SyntaxKind.JSDocImportTag)
        {
            Node? specifier = null;
            if (node.ImportClause() is { } importClause)
            {
                specifier = GetPotentiallyInvalidImportSpecifier(importClause.AsImportClause().NamedBindings);
            }

            potentialSplitPoint = specifier ?? node.ModuleSpecifier();
        }
        else
        {
            potentialSplitPoint = node.AsImportEqualsDeclaration().ModuleReference;
        }

        var withoutModuleSpecifier = new global::Ts.Core.TextRange(
            global::Ts.Scanner.Scanner.GetTokenPosOfNode(LsDeps.GetFirstToken(node, sourceFile)!, sourceFile, false),
            potentialSplitPoint!.Pos);
        // The module specifier/reference was previously found to be missing, empty, or
        // not a string literal - in this last case, it's likely that statement on a following
        // line was parsed as the module specifier of a partially-typed import, e.g.
        //   import Foo|
        //   interface Blah {}
        // This appears to be a multiline-import, and editors can't replace multiple lines.
        // But if everything but the "module specifier" is on one line, by this point we can
        // assume that the "module specifier" is actually just another statement, and return
        // the single-line range of the import excluding that probable statement.
        if (global::Ts.Printer.PrinterUtilities.GetLinesBetweenPositions(sourceFile, withoutModuleSpecifier.Pos, withoutModuleSpecifier.End) == 0)
        {
            return CreateLspRangeFromBounds(withoutModuleSpecifier.Pos, withoutModuleSpecifier.End, sourceFile);
        }
        return null;
    }

    // completions.go:5260-5262 — couldBeTypeOnlyImportSpecifier.
    internal static bool CouldBeTypeOnlyImportSpecifier(Node importSpecifier, Node contextToken)
    {
        return NodeTests.IsImportSpecifier(importSpecifier) &&
            (importSpecifier.IsTypeOnly() || (contextToken == importSpecifier.Name() && IsTypeKeywordTokenOrIdentifier(contextToken)));
    }

    // completions.go:5264-5290 — canCompleteFromNamedBindings.
    internal static bool CanCompleteFromNamedBindings(Node namedBindings)
    {
        if (!IsModuleSpecifierMissingOrEmpty(namedBindings.Parent!.Parent!.ModuleSpecifier()!) || namedBindings.Parent.Name() != null)
        {
            return false;
        }
        if (NodeTests.IsNamedImports(namedBindings))
        {
            // We can only complete on named imports if there are no other named imports already,
            // but parser recovery sometimes puts later statements in the named imports list, so
            // we try to only consider the probably-valid ones.
            var invalidNamedImport = GetPotentiallyInvalidImportSpecifier(namedBindings);
            var elements = namedBindings.Elements() ?? [];
            var validImports = elements.Length;
            if (invalidNamedImport != null)
            {
                validImports = Array.IndexOf(elements, invalidNamedImport);
            }

            return validImports < 2 && validImports > -1;
        }
        return true;
    }

    // completions.go:5292-5300 — getPotentiallyInvalidImportSpecifier: tries to identify the first
    // named import that is not really a named import, but rather just parser recovery.
    internal static Node? GetPotentiallyInvalidImportSpecifier(Node? namedBindings)
    {
        if (namedBindings == null || namedBindings.Kind != SyntaxKind.NamedImports)
        {
            return null;
        }
        return (namedBindings.Elements() ?? []).FirstOrDefault(e =>
            e.PropertyName() == null && LsDeps.IsNonContextualKeyword(global::Ts.Scanner.Scanner.StringToToken(e.Name()!.Text())) &&
            global::Ts.AstNav.Tokens.FindPrecedingToken(AstUtilities.GetSourceFileOfNode(namedBindings)!, e.Name()!.Pos)?.Kind != SyntaxKind.CommaToken);
    }

    // completions.go:5302-5313 — isModuleSpecifierMissingOrEmpty.
    internal static bool IsModuleSpecifierMissingOrEmpty(Node? specifier)
    {
        if (AstUtilities.NodeIsMissing(specifier))
        {
            return true;
        }
        var node = specifier!;
        if (NodeTests.IsExternalModuleReference(node))
        {
            node = node.Expression()!;
        }
        if (!AstUtilities.IsStringLiteralLike(node))
        {
            return true;
        }
        return node.Text().Length == 0;
    }

    // completions.go:5315-5318 — hasDocComment.
    internal static bool HasDocComment(SourceFile file, int position)
    {
        var token = global::Ts.AstNav.Tokens.GetTokenAtPosition(file, position);
        return AstUtilities.FindAncestor(token, n => n.Kind == SyntaxKind.JSDoc) != null;
    }
}
