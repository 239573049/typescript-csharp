// Ported from tsgo internal/ast/ast.go (CommentDirective / CommentDirectiveKind).
// Predeclared here to support the scanner (phase 2); the rest of ast.go lands in phase 3.

using Ts.Core;

namespace Ts.Ast;

// Ported from tsgo internal/ast/ast.go:2381-2385
public enum CommentDirectiveKind
{
    Unknown,     // CommentDirectiveKindUnknown
    ExpectError, // CommentDirectiveKindExpectError
    Ignore,      // CommentDirectiveKindIgnore
}

// Ported from tsgo internal/ast/ast.go:2387-2390
public readonly record struct CommentDirective(TextRange Loc, CommentDirectiveKind Kind);
