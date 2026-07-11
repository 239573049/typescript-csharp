// Ported from tsgo internal/parser/parser.go (list terminator/error section).
using Ts.Ast;

namespace Ts.Parser;

public sealed partial class Parser
{
    private bool IsListTerminator(ParsingContext kind)
    {
        if (token == SyntaxKind.EndOfFile)
        {
            return true;
        }
        switch (kind)
        {
            case ParsingContext.BlockStatements:
            case ParsingContext.SwitchClauses:
            case ParsingContext.TypeMembers:
            case ParsingContext.ClassMembers:
            case ParsingContext.EnumMembers:
            case ParsingContext.ObjectLiteralMembers:
            case ParsingContext.ObjectBindingElements:
            case ParsingContext.ImportOrExportSpecifiers:
            case ParsingContext.ImportAttributes:
                return token == SyntaxKind.CloseBraceToken;
            case ParsingContext.SwitchClauseStatements:
                return token == SyntaxKind.CloseBraceToken || token == SyntaxKind.CaseKeyword || token == SyntaxKind.DefaultKeyword;
            case ParsingContext.HeritageClauseElement:
                return token == SyntaxKind.OpenBraceToken || token == SyntaxKind.ExtendsKeyword || token == SyntaxKind.ImplementsKeyword;
            case ParsingContext.VariableDeclarations:
                return CanParseSemicolon() || token == SyntaxKind.InKeyword || token == SyntaxKind.OfKeyword || token == SyntaxKind.EqualsGreaterThanToken;
            case ParsingContext.TypeParameters:
                return token == SyntaxKind.GreaterThanToken || token == SyntaxKind.OpenParenToken || token == SyntaxKind.OpenBraceToken || token == SyntaxKind.ExtendsKeyword || token == SyntaxKind.ImplementsKeyword;
            case ParsingContext.ArgumentExpressions:
                return token == SyntaxKind.CloseParenToken || token == SyntaxKind.SemicolonToken;
            case ParsingContext.ArrayLiteralMembers:
            case ParsingContext.TupleElementTypes:
            case ParsingContext.ArrayBindingElements:
                return token == SyntaxKind.CloseBracketToken;
            case ParsingContext.JSDocParameters:
            case ParsingContext.Parameters:
            case ParsingContext.RestProperties:
                return token == SyntaxKind.CloseParenToken || token == SyntaxKind.CloseBracketToken;
            case ParsingContext.TypeArguments:
                // All other tokens should cause the type-argument to terminate except comma token
                return token != SyntaxKind.CommaToken;
            case ParsingContext.HeritageClauses:
                return token == SyntaxKind.OpenBraceToken || token == SyntaxKind.CloseBraceToken;
            case ParsingContext.JsxAttributes:
                return token == SyntaxKind.GreaterThanToken || token == SyntaxKind.SlashToken;
            case ParsingContext.JsxChildren:
                return token == SyntaxKind.LessThanToken && LookAhead(NextTokenIsSlash);
        }
        return false;
    }

    private void ParsingContextErrors(ParsingContext context)
    {
        switch (context)
        {
            case ParsingContext.SourceElements:
                if (token == SyntaxKind.DefaultKeyword)
                {
                    ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_0_expected, "export");
                }
                else
                {
                    ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Declaration_or_statement_expected);
                }
                break;
            case ParsingContext.BlockStatements:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Declaration_or_statement_expected);
                break;
            case ParsingContext.SwitchClauses:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_case_or_default_expected);
                break;
            case ParsingContext.SwitchClauseStatements:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Statement_expected);
                break;
            case ParsingContext.RestProperties:
            case ParsingContext.TypeMembers:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Property_or_signature_expected);
                break;
            case ParsingContext.ClassMembers:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Unexpected_token_A_constructor_method_accessor_or_property_was_expected);
                break;
            case ParsingContext.EnumMembers:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Enum_member_expected);
                break;
            case ParsingContext.HeritageClauseElement:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Expression_expected);
                break;
            case ParsingContext.VariableDeclarations:
                if (NodeTests.IsKeywordKind(token))
                {
                    ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_0_is_not_allowed_as_a_variable_declaration_name, global::Ts.Scanner.Scanner.TokenToString(token));
                }
                else
                {
                    ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Variable_declaration_expected);
                }
                break;
            case ParsingContext.ObjectBindingElements:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Property_destructuring_pattern_expected);
                break;
            case ParsingContext.ArrayBindingElements:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Array_element_destructuring_pattern_expected);
                break;
            case ParsingContext.ArgumentExpressions:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Argument_expression_expected);
                break;
            case ParsingContext.ObjectLiteralMembers:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Property_assignment_expected);
                break;
            case ParsingContext.ArrayLiteralMembers:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Expression_or_comma_expected);
                break;
            case ParsingContext.JSDocParameters:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Parameter_declaration_expected);
                break;
            case ParsingContext.Parameters:
                if (NodeTests.IsKeywordKind(token))
                {
                    ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_0_is_not_allowed_as_a_parameter_name, global::Ts.Scanner.Scanner.TokenToString(token));
                }
                else
                {
                    ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Parameter_declaration_expected);
                }
                break;
            case ParsingContext.TypeParameters:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Type_parameter_declaration_expected);
                break;
            case ParsingContext.TypeArguments:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Type_argument_expected);
                break;
            case ParsingContext.TupleElementTypes:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Type_expected);
                break;
            case ParsingContext.HeritageClauses:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Unexpected_token_expected);
                break;
            case ParsingContext.ImportOrExportSpecifiers:
                if (token == SyntaxKind.FromKeyword)
                {
                    ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_0_expected, "}");
                }
                else
                {
                    ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Identifier_expected);
                }
                break;
            case ParsingContext.JsxAttributes:
            case ParsingContext.JsxChildren:
            case ParsingContext.JSDocComment:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Identifier_expected);
                break;
            case ParsingContext.ImportAttributes:
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Identifier_or_string_literal_expected);
                break;
            default:
                throw new System.InvalidOperationException("Unhandled case in parsingContextErrors");
        }
    }

    private bool NextTokenIsSlash() => NextToken() == SyntaxKind.SlashToken;
}
