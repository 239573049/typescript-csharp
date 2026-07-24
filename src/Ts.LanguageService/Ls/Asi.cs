// Ported from tsgo internal/ls/lsutil/asi.go (whole file): automatic-semicolon-insertion
// candidate detection shared by completions (await insertion) and formatting.

using Ts.Ast;

namespace Ts.Ls;

internal static class Asi
{
    // asi.go:9-19 — PositionIsASICandidate.
    internal static bool PositionIsASICandidate(int pos, Node context, SourceFile file)
    {
        var contextAncestor = AstUtilities.FindAncestorOrQuit(context, ancestor =>
        {
            if (ancestor.End != pos)
            {
                return FindAncestorResult.Quit;
            }

            return AstUtilities.ToFindAncestorResult(SyntaxMayBeASICandidate(ancestor.Kind));
        });

        return contextAncestor != null && NodeIsASICandidate(contextAncestor, file);
    }

    // asi.go:21-26 — SyntaxMayBeASICandidate.
    internal static bool SyntaxMayBeASICandidate(SyntaxKind kind)
    {
        return SyntaxRequiresTrailingCommaOrSemicolonOrASI(kind) ||
            SyntaxRequiresTrailingFunctionBlockOrSemicolonOrASI(kind) ||
            SyntaxRequiresTrailingModuleBlockOrSemicolonOrASI(kind) ||
            SyntaxRequiresTrailingSemicolonOrASI(kind);
    }

    // asi.go:28-34 — SyntaxRequiresTrailingCommaOrSemicolonOrASI.
    internal static bool SyntaxRequiresTrailingCommaOrSemicolonOrASI(SyntaxKind kind)
    {
        return kind == SyntaxKind.CallSignature ||
            kind == SyntaxKind.ConstructSignature ||
            kind == SyntaxKind.IndexSignature ||
            kind == SyntaxKind.PropertySignature ||
            kind == SyntaxKind.MethodSignature;
    }

    // asi.go:36-42 — SyntaxRequiresTrailingFunctionBlockOrSemicolonOrASI.
    internal static bool SyntaxRequiresTrailingFunctionBlockOrSemicolonOrASI(SyntaxKind kind)
    {
        return kind == SyntaxKind.FunctionDeclaration ||
            kind == SyntaxKind.Constructor ||
            kind == SyntaxKind.MethodDeclaration ||
            kind == SyntaxKind.GetAccessor ||
            kind == SyntaxKind.SetAccessor;
    }

    // asi.go:44-46 — SyntaxRequiresTrailingModuleBlockOrSemicolonOrASI.
    internal static bool SyntaxRequiresTrailingModuleBlockOrSemicolonOrASI(SyntaxKind kind)
    {
        return kind == SyntaxKind.ModuleDeclaration;
    }

    // asi.go:48-64 — SyntaxRequiresTrailingSemicolonOrASI.
    internal static bool SyntaxRequiresTrailingSemicolonOrASI(SyntaxKind kind)
    {
        return kind == SyntaxKind.VariableStatement ||
            kind == SyntaxKind.ExpressionStatement ||
            kind == SyntaxKind.DoStatement ||
            kind == SyntaxKind.ContinueStatement ||
            kind == SyntaxKind.BreakStatement ||
            kind == SyntaxKind.ReturnStatement ||
            kind == SyntaxKind.ThrowStatement ||
            kind == SyntaxKind.DebuggerStatement ||
            kind == SyntaxKind.PropertyDeclaration ||
            kind == SyntaxKind.TypeAliasDeclaration ||
            kind == SyntaxKind.ImportDeclaration ||
            kind == SyntaxKind.ImportEqualsDeclaration ||
            kind == SyntaxKind.ExportDeclaration ||
            kind == SyntaxKind.NamespaceExportDeclaration ||
            kind == SyntaxKind.ExportAssignment;
    }

    // asi.go:66-104 — NodeIsASICandidate.
    internal static bool NodeIsASICandidate(Node node, SourceFile file)
    {
        var lastToken = LsDeps.GetLastToken(node, file);
        if (lastToken != null && lastToken.Kind == SyntaxKind.SemicolonToken)
        {
            return false;
        }

        if (SyntaxRequiresTrailingCommaOrSemicolonOrASI(node.Kind))
        {
            if (lastToken != null && lastToken.Kind == SyntaxKind.CommaToken)
            {
                return false;
            }
        }
        else if (SyntaxRequiresTrailingModuleBlockOrSemicolonOrASI(node.Kind))
        {
            var lastChild = LsDeps.GetLastChild(node, file);
            if (lastChild != null && NodeTests.IsModuleBlock(lastChild))
            {
                return false;
            }
        }
        else if (SyntaxRequiresTrailingFunctionBlockOrSemicolonOrASI(node.Kind))
        {
            var lastChild = LsDeps.GetLastChild(node, file);
            if (lastChild != null && AstUtilities.IsFunctionBlock(lastChild))
            {
                return false;
            }
        }
        else if (!SyntaxRequiresTrailingSemicolonOrASI(node.Kind))
        {
            return false;
        }

        // See comment in parser's `parseDoStatement`
        if (node.Kind == SyntaxKind.DoStatement)
        {
            return true;
        }

        var topNode = AstUtilities.FindAncestor(node, ancestor => ancestor.Parent == null)!;
        var nextToken = global::Ts.AstNav.Tokens.FindNextToken(node, topNode, file);
        if (nextToken == null || nextToken.Kind == SyntaxKind.CloseBraceToken)
        {
            return true;
        }

        var startLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(file, node.End);
        var endLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(file, global::Ts.AstNav.Tokens.GetStartOfNode(nextToken, file, false /*includeJSDoc*/));
        return startLine != endLine;
    }

    // lsutil/utilities.go:16-77 — ProbablyUsesSemicolons.
    internal static bool ProbablyUsesSemicolons(SourceFile file)
    {
        var withSemicolon = 0;
        var withoutSemicolon = 0;
        const int nStatementsToObserve = 5;

        bool Visit(Node node)
        {
            if ((node.Flags & NodeFlags.Reparsed) != 0)
            {
                return false;
            }
            if (SyntaxRequiresTrailingSemicolonOrASI(node.Kind))
            {
                var lastToken = LsDeps.GetLastToken(node, file);
                if (lastToken != null && lastToken.Kind == SyntaxKind.SemicolonToken)
                {
                    withSemicolon++;
                }
                else
                {
                    withoutSemicolon++;
                }
            }
            else if (SyntaxRequiresTrailingCommaOrSemicolonOrASI(node.Kind))
            {
                var lastToken = LsDeps.GetLastToken(node, file);
                if (lastToken != null && lastToken.Kind == SyntaxKind.SemicolonToken)
                {
                    withSemicolon++;
                }
                else if (lastToken != null && lastToken.Kind != SyntaxKind.CommaToken)
                {
                    var lastTokenLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(
                        file,
                        global::Ts.AstNav.Tokens.GetStartOfNode(lastToken, file, false /*includeJSDoc*/));
                    var nextTokenLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(
                        file,
                        global::Ts.Scanner.Scanner.SkipTrivia(file.Text(), lastToken.End));
                    // Avoid counting missing semicolon in single-line objects:
                    // `function f(p: { x: string /*no semicolon here is insignificant*/ }) {`
                    if (lastTokenLine != nextTokenLine)
                    {
                        withoutSemicolon++;
                    }
                }
            }

            if (withSemicolon + withoutSemicolon >= nStatementsToObserve)
            {
                return true;
            }

            return node.ForEachChild(Visit);
        }

        file.ForEachChild(Visit);

        // One statement missing a semicolon isn't sufficient evidence to say the user
        // doesn't want semicolons, because they may not even be done writing that statement.
        if (withSemicolon == 0 && withoutSemicolon <= 1)
        {
            return true;
        }

        // When both kinds of observation exist, treat the file as using semicolons when the
        // ratio withSemicolon/withoutSemicolon exceeds 1/nStatementsToObserve (real arithmetic),
        // implemented as an integer inequality to avoid truncation.
        if (withoutSemicolon == 0)
        {
            return true;
        }
        return withSemicolon * nStatementsToObserve > withoutSemicolon;
    }
}
