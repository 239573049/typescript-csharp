// Ported from tsgo internal/parser/parser.go (list-element / start-of predicates).
using System;

using Ts.Ast;

namespace Ts.Parser;

public sealed partial class Parser
{
    private bool IsListElement(ParsingContext parsingContext, bool inErrorRecovery)
    {
        switch (parsingContext)
        {
            case ParsingContext.SourceElements:
            case ParsingContext.BlockStatements:
            case ParsingContext.SwitchClauseStatements:
                // In error recovery we don't want to treat ';' as an empty statement.
                return !(token == SyntaxKind.SemicolonToken && inErrorRecovery) && IsStartOfStatement();
            case ParsingContext.SwitchClauses:
                return token == SyntaxKind.CaseKeyword || token == SyntaxKind.DefaultKeyword;
            case ParsingContext.TypeMembers:
                return LookAhead(ScanTypeMemberStart);
            case ParsingContext.ClassMembers:
                return LookAhead(ScanClassMemberStart) || (token == SyntaxKind.SemicolonToken && !inErrorRecovery);
            case ParsingContext.EnumMembers:
                // Include open bracket computed properties.
                return token == SyntaxKind.OpenBracketToken || IsLiteralPropertyName();
            case ParsingContext.ObjectLiteralMembers:
                switch (token)
                {
                    case SyntaxKind.OpenBracketToken:
                    case SyntaxKind.AsteriskToken:
                    case SyntaxKind.DotDotDotToken:
                    case SyntaxKind.DotToken: // Not a member, but don't want to close the object
                        return true;
                    default:
                        return IsLiteralPropertyName();
                }
            case ParsingContext.RestProperties:
                return IsLiteralPropertyName();
            case ParsingContext.ObjectBindingElements:
                return token == SyntaxKind.OpenBracketToken || token == SyntaxKind.DotDotDotToken || IsLiteralPropertyName();
            case ParsingContext.ImportAttributes:
                return IsImportAttributeName();
            case ParsingContext.HeritageClauseElement:
                // If we see `{ ... }` only consume it as an expression if followed by `,` or `{`.
                if (token == SyntaxKind.OpenBraceToken)
                {
                    return IsValidHeritageClauseObjectLiteral();
                }
                if (!inErrorRecovery)
                {
                    return IsStartOfLeftHandSideExpression() && !IsHeritageClauseExtendsOrImplementsKeyword();
                }
                // In error recovery we tighten up what we're willing to match.
                return IsIdentifier() && !IsHeritageClauseExtendsOrImplementsKeyword();
            case ParsingContext.VariableDeclarations:
                return IsBindingIdentifierOrPrivateIdentifierOrPattern();
            case ParsingContext.ArrayBindingElements:
                return token == SyntaxKind.CommaToken || token == SyntaxKind.DotDotDotToken || IsBindingIdentifierOrPrivateIdentifierOrPattern();
            case ParsingContext.TypeParameters:
                return token == SyntaxKind.InKeyword || token == SyntaxKind.ConstKeyword || IsIdentifier();
            case ParsingContext.ArrayLiteralMembers:
                // Not an array literal member, but don't want to close the array.
                if (token == SyntaxKind.CommaToken || token == SyntaxKind.DotToken)
                {
                    return true;
                }
                goto case ParsingContext.ArgumentExpressions;
            case ParsingContext.ArgumentExpressions:
                return token == SyntaxKind.DotDotDotToken || IsStartOfExpression();
            case ParsingContext.Parameters:
                return IsStartOfParameter(false);
            case ParsingContext.JSDocParameters:
                return IsStartOfParameter(true);
            case ParsingContext.TypeArguments:
            case ParsingContext.TupleElementTypes:
                return token == SyntaxKind.CommaToken || IsStartOfType(false);
            case ParsingContext.HeritageClauses:
                return IsHeritageClause();
            case ParsingContext.ImportOrExportSpecifiers:
                // bail out if the next token is [FromKeyword StringLiteral].
                if (token == SyntaxKind.FromKeyword && LookAhead(NextTokenIsTokenStringLiteral))
                {
                    return false;
                }
                if (token == SyntaxKind.StringLiteral)
                {
                    return true; // For "arbitrary module namespace identifiers"
                }
                return Utilities.TokenIsIdentifierOrKeyword(token);
            case ParsingContext.JsxAttributes:
                return Utilities.TokenIsIdentifierOrKeyword(token) || token == SyntaxKind.OpenBraceToken;
            case ParsingContext.JsxChildren:
                return true;
            case ParsingContext.JSDocComment:
                return true;
        }
        throw new InvalidOperationException("Unhandled case in isListElement");
    }

    private bool IsStartOfStatement()
    {
        switch (token)
        {
            // 'catch' and 'finally' don't indicate a statement, but we say they do to parse gracefully and error later.
            case SyntaxKind.AtToken:
            case SyntaxKind.SemicolonToken:
            case SyntaxKind.OpenBraceToken:
            case SyntaxKind.VarKeyword:
            case SyntaxKind.LetKeyword:
            case SyntaxKind.UsingKeyword:
            case SyntaxKind.FunctionKeyword:
            case SyntaxKind.ClassKeyword:
            case SyntaxKind.EnumKeyword:
            case SyntaxKind.IfKeyword:
            case SyntaxKind.DoKeyword:
            case SyntaxKind.WhileKeyword:
            case SyntaxKind.ForKeyword:
            case SyntaxKind.ContinueKeyword:
            case SyntaxKind.BreakKeyword:
            case SyntaxKind.ReturnKeyword:
            case SyntaxKind.WithKeyword:
            case SyntaxKind.SwitchKeyword:
            case SyntaxKind.ThrowKeyword:
            case SyntaxKind.TryKeyword:
            case SyntaxKind.DebuggerKeyword:
            case SyntaxKind.CatchKeyword:
            case SyntaxKind.FinallyKeyword:
                return true;
            case SyntaxKind.ImportKeyword:
                return IsStartOfDeclaration() || IsNextTokenOpenParenOrLessThanOrDot();
            case SyntaxKind.ConstKeyword:
            case SyntaxKind.ExportKeyword:
                return IsStartOfDeclaration();
            case SyntaxKind.AsyncKeyword:
            case SyntaxKind.DeclareKeyword:
            case SyntaxKind.InterfaceKeyword:
            case SyntaxKind.ModuleKeyword:
            case SyntaxKind.NamespaceKeyword:
            case SyntaxKind.TypeKeyword:
            case SyntaxKind.GlobalKeyword:
            case SyntaxKind.DeferKeyword:
                // When these don't start a declaration, they're an identifier in an expression statement.
                return true;
            case SyntaxKind.AccessorKeyword:
            case SyntaxKind.PublicKeyword:
            case SyntaxKind.PrivateKeyword:
            case SyntaxKind.ProtectedKeyword:
            case SyntaxKind.StaticKeyword:
            case SyntaxKind.ReadonlyKeyword:
                // When these don't start a declaration, they may start a class member if an identifier follows.
                return IsStartOfDeclaration() || !LookAhead(NextTokenIsIdentifierOrKeywordOnSameLine);
            default:
                return IsStartOfExpression();
        }
    }

    private bool IsStartOfExpression()
    {
        if (IsStartOfLeftHandSideExpression())
        {
            return true;
        }
        switch (token)
        {
            case SyntaxKind.PlusToken:
            case SyntaxKind.MinusToken:
            case SyntaxKind.TildeToken:
            case SyntaxKind.ExclamationToken:
            case SyntaxKind.DeleteKeyword:
            case SyntaxKind.TypeOfKeyword:
            case SyntaxKind.VoidKeyword:
            case SyntaxKind.PlusPlusToken:
            case SyntaxKind.MinusMinusToken:
            case SyntaxKind.LessThanToken:
            case SyntaxKind.AwaitKeyword:
            case SyntaxKind.YieldKeyword:
            case SyntaxKind.PrivateIdentifier:
            case SyntaxKind.AtToken:
                // Yield/await always start an expression.
                return true;
        }
        // Error tolerance: the start of a binary operator counts as the start of an expression.
        if (IsBinaryOperator())
        {
            return true;
        }
        return IsIdentifier();
    }

    private bool IsStartOfLeftHandSideExpression()
    {
        switch (token)
        {
            case SyntaxKind.ThisKeyword:
            case SyntaxKind.SuperKeyword:
            case SyntaxKind.NullKeyword:
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.TemplateHead:
            case SyntaxKind.OpenParenToken:
            case SyntaxKind.OpenBracketToken:
            case SyntaxKind.OpenBraceToken:
            case SyntaxKind.FunctionKeyword:
            case SyntaxKind.ClassKeyword:
            case SyntaxKind.NewKeyword:
            case SyntaxKind.SlashToken:
            case SyntaxKind.SlashEqualsToken:
            case SyntaxKind.Identifier:
                return true;
            case SyntaxKind.ImportKeyword:
                return IsNextTokenOpenParenOrLessThanOrDot();
        }
        return IsIdentifier();
    }

    private bool IsIdentifier()
    {
        if (token == SyntaxKind.Identifier)
        {
            return true;
        }
        // 'yield' in [Yield] context and 'await' in [Await] context are keywords, not identifiers.
        if ((token == SyntaxKind.YieldKeyword && InYieldContext()) || (token == SyntaxKind.AwaitKeyword && InAwaitContext()))
        {
            return false;
        }
        return token > SyntaxKind.LastReservedWord;
    }

    private bool IsBinaryOperator()
    {
        if (InDisallowInContext() && token == SyntaxKind.InKeyword)
        {
            return false;
        }
        return Precedence.GetBinaryOperatorPrecedence(token) != OperatorPrecedence.Invalid;
    }
}
