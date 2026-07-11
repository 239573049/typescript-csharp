// AST-gaps required by the parser Statements cluster (src/Ts.Compiler/Parser/Parser.Statements.cs).
//
// These are ast-package predicates that the port needs but that are not yet present in the
// generated NodeTests (a non-partial generated class, so it cannot be extended here) or the
// hand-written AstUtilities. Each was grep-confirmed absent on 2026-07-11. They are 1:1 ports
// of the corresponding tsgo functions in internal/ast/utilities.go.
#nullable enable

namespace Ts.Ast;

public static class ParserStatementsAstGaps
{
    // Go: ast.IsQuestionToken (internal/ast/utilities.go:2086)
    public static bool IsQuestionToken(Node? node) => node != null && node.Kind == SyntaxKind.QuestionToken;
}
