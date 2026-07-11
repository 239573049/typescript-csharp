// Ported from tsgo internal/format/rulecontext.go

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Format;

internal static class RuleContext
{
    ///
    /// Contexts
    ///

    // Go: type optionSelector = func(options lsutil.FormatCodeSettings) core.Tristate
    internal delegate Tristate OptionSelector(FormatCodeSettings options);

    // Go: type anyOptionSelector[T comparable] = func(options lsutil.FormatCodeSettings) T
    internal delegate T AnyOptionSelector<T>(FormatCodeSettings options);

    internal static SemicolonPreference SemicolonOption(FormatCodeSettings options) => options.Semicolons;

    internal static Tristate InsertSpaceAfterCommaDelimiterOption(FormatCodeSettings options) => options.InsertSpaceAfterCommaDelimiter;

    internal static Tristate InsertSpaceAfterSemicolonInForStatementsOption(FormatCodeSettings options) => options.InsertSpaceAfterSemicolonInForStatements;

    internal static Tristate InsertSpaceBeforeAndAfterBinaryOperatorsOption(FormatCodeSettings options) => options.InsertSpaceBeforeAndAfterBinaryOperators;

    internal static Tristate InsertSpaceAfterConstructorOption(FormatCodeSettings options) => options.InsertSpaceAfterConstructor;

    internal static Tristate InsertSpaceAfterKeywordsInControlFlowStatementsOption(FormatCodeSettings options) => options.InsertSpaceAfterKeywordsInControlFlowStatements;

    internal static Tristate InsertSpaceAfterFunctionKeywordForAnonymousFunctionsOption(FormatCodeSettings options) => options.InsertSpaceAfterFunctionKeywordForAnonymousFunctions;

    internal static Tristate InsertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesisOption(FormatCodeSettings options) => options.InsertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesis;

    internal static Tristate InsertSpaceAfterOpeningAndBeforeClosingNonemptyBracketsOption(FormatCodeSettings options) => options.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBrackets;

    internal static Tristate InsertSpaceAfterOpeningAndBeforeClosingNonemptyBracesOption(FormatCodeSettings options) => options.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBraces;

    internal static Tristate InsertSpaceAfterOpeningAndBeforeClosingEmptyBracesOption(FormatCodeSettings options) => options.InsertSpaceAfterOpeningAndBeforeClosingEmptyBraces;

    internal static Tristate InsertSpaceAfterOpeningAndBeforeClosingTemplateStringBracesOption(FormatCodeSettings options) => options.InsertSpaceAfterOpeningAndBeforeClosingTemplateStringBraces;

    internal static Tristate InsertSpaceAfterOpeningAndBeforeClosingJsxExpressionBracesOption(FormatCodeSettings options) => options.InsertSpaceAfterOpeningAndBeforeClosingJsxExpressionBraces;

    internal static Tristate InsertSpaceAfterTypeAssertionOption(FormatCodeSettings options) => options.InsertSpaceAfterTypeAssertion;

    internal static Tristate InsertSpaceBeforeFunctionParenthesisOption(FormatCodeSettings options) => options.InsertSpaceBeforeFunctionParenthesis;

    internal static Tristate PlaceOpenBraceOnNewLineForFunctionsOption(FormatCodeSettings options) => options.PlaceOpenBraceOnNewLineForFunctions;

    internal static Tristate PlaceOpenBraceOnNewLineForControlBlocksOption(FormatCodeSettings options) => options.PlaceOpenBraceOnNewLineForControlBlocks;

    internal static Tristate InsertSpaceBeforeTypeAnnotationOption(FormatCodeSettings options) => options.InsertSpaceBeforeTypeAnnotation;

    internal static Tristate IndentMultiLineObjectLiteralBeginningOnBlankLineOption(FormatCodeSettings options) => options.IndentMultiLineObjectLiteralBeginningOnBlankLine;

    internal static Tristate IndentSwitchCaseOption(FormatCodeSettings options) => options.IndentSwitchCase;

    // Go: func optionEquals[T comparable](optionName, optionValue) contextPredicate
    internal static ContextPredicate OptionEquals<T>(AnyOptionSelector<T> optionName, T optionValue)
    {
        return context => System.Collections.Generic.EqualityComparer<T>.Default.Equals(optionName(context.Options), optionValue);
    }

    // Go: func isOptionEnabled(optionName optionSelector) contextPredicate
    internal static ContextPredicate IsOptionEnabled(OptionSelector optionName)
        => context => optionName(context.Options).IsTrue();

    // Go: func isOptionDisabled(optionName optionSelector) contextPredicate
    internal static ContextPredicate IsOptionDisabled(OptionSelector optionName)
        => context => optionName(context.Options).IsFalse();

    // Go: func isOptionDisabledOrUndefined(optionName optionSelector) contextPredicate
    internal static ContextPredicate IsOptionDisabledOrUndefined(OptionSelector optionName)
        => context => optionName(context.Options).IsFalseOrUnknown();

    // Go: func isOptionDisabledOrUndefinedOrTokensOnSameLine(optionName optionSelector) contextPredicate
    internal static ContextPredicate IsOptionDisabledOrUndefinedOrTokensOnSameLine(OptionSelector optionName)
        => context => optionName(context.Options).IsFalseOrUnknown() || context.TokensAreOnSameLine();

    // Go: func isOptionEnabledOrUndefined(optionName optionSelector) contextPredicate
    internal static ContextPredicate IsOptionEnabledOrUndefined(OptionSelector optionName)
        => context => optionName(context.Options).IsTrueOrUnknown();

    internal static bool IsForContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.ForStatement;

    internal static bool IsNotForContext(FormattingContext context) => !IsForContext(context);

    internal static bool IsBinaryOpContext(FormattingContext context)
    {
        switch (context.ContextNode!.Kind)
        {
            case SyntaxKind.BinaryExpression:
                return context.ContextNode.AsBinaryExpression().OperatorToken!.Kind != SyntaxKind.CommaToken;
            case SyntaxKind.ConditionalExpression:
            case SyntaxKind.ConditionalType:
            case SyntaxKind.AsExpression:
            case SyntaxKind.ExportSpecifier:
            case SyntaxKind.ImportSpecifier:
            case SyntaxKind.TypePredicate:
            case SyntaxKind.UnionType:
            case SyntaxKind.IntersectionType:
            case SyntaxKind.SatisfiesExpression:
                return true;

            // equals in binding elements func foo([[x, y] = [1, 2]])
            case SyntaxKind.BindingElement:
            // equals in type X = ...
            case SyntaxKind.TypeAliasDeclaration:
            // equal in import a = module('a');
            case SyntaxKind.ImportEqualsDeclaration:
            // equal in export = 1
            case SyntaxKind.ExportAssignment:
            // equal in let a = 0
            case SyntaxKind.VariableDeclaration:
            // equal in p = 0
            case SyntaxKind.Parameter:
            case SyntaxKind.EnumMember:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
                return context.CurrentTokenSpan.Kind == SyntaxKind.EqualsToken || context.NextTokenSpan.Kind == SyntaxKind.EqualsToken;
            // "in" keyword in for (let x in []) { }
            case SyntaxKind.ForInStatement:
            // "in" keyword in [P in keyof T] T[P]
            case SyntaxKind.TypeParameter:
                return context.CurrentTokenSpan.Kind == SyntaxKind.InKeyword || context.NextTokenSpan.Kind == SyntaxKind.InKeyword || context.CurrentTokenSpan.Kind == SyntaxKind.EqualsToken || context.NextTokenSpan.Kind == SyntaxKind.EqualsToken;
            // Technically, "of" is not a binary operator, but format it the same way as "in"
            case SyntaxKind.ForOfStatement:
                return context.CurrentTokenSpan.Kind == SyntaxKind.OfKeyword || context.NextTokenSpan.Kind == SyntaxKind.OfKeyword;
        }
        return false;
    }

    internal static bool IsNotBinaryOpContext(FormattingContext context) => !IsBinaryOpContext(context);

    internal static bool IsNotTypeAnnotationContext(FormattingContext context) => !IsTypeAnnotationContext(context);

    internal static bool IsTypeAnnotationContext(FormattingContext context)
    {
        SyntaxKind contextKind = context.ContextNode!.Kind;
        return contextKind == SyntaxKind.PropertyDeclaration ||
               contextKind == SyntaxKind.PropertySignature ||
               contextKind == SyntaxKind.Parameter ||
               contextKind == SyntaxKind.VariableDeclaration ||
               AstUtilities.IsFunctionLikeKind(contextKind);
    }

    internal static bool IsOptionalPropertyContext(FormattingContext context)
        => NodeTests.IsPropertyDeclaration(context.ContextNode!) && AstUtilities.HasQuestionToken(context.ContextNode!);

    internal static bool IsNonOptionalPropertyContext(FormattingContext context) => !IsOptionalPropertyContext(context);

    internal static bool IsConditionalOperatorContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.ConditionalExpression ||
           context.ContextNode.Kind == SyntaxKind.ConditionalType;

    internal static bool IsSameLineTokenOrBeforeBlockContext(FormattingContext context)
        => context.TokensAreOnSameLine() || IsBeforeBlockContext(context);

    internal static bool IsBraceWrappedContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.ObjectBindingPattern ||
           context.ContextNode.Kind == SyntaxKind.MappedType ||
           IsSingleLineBlockContext(context);

    // This check is done before an open brace in a control construct, a function, or a typescript block declaration
    internal static bool IsBeforeMultilineBlockContext(FormattingContext context)
        => IsBeforeBlockContext(context) && !(context.NextNodeAllOnSameLine() || context.NextNodeBlockIsOnOneLine());

    internal static bool IsMultilineBlockContext(FormattingContext context)
        => IsBlockContext(context) && !(context.ContextNodeAllOnSameLine() || context.ContextNodeBlockIsOnOneLine());

    internal static bool IsSingleLineBlockContext(FormattingContext context)
        => IsBlockContext(context) && (context.ContextNodeAllOnSameLine() || context.ContextNodeBlockIsOnOneLine());

    internal static bool IsBlockContext(FormattingContext context) => NodeIsBlockContext(context.ContextNode!);

    internal static bool IsBeforeBlockContext(FormattingContext context) => NodeIsBlockContext(context.NextTokenParent!);

    // IMPORTANT!!! This method must return true ONLY for nodes with open and close braces as immediate children
    internal static bool NodeIsBlockContext(Node node)
    {
        if (NodeIsTypeScriptDeclWithBlockContext(node))
        {
            return true;
        }

        switch (node.Kind)
        {
            case SyntaxKind.Block:
            case SyntaxKind.CaseBlock:
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.ModuleBlock:
                return true;
        }

        return false;
    }

    internal static bool IsFunctionDeclContext(FormattingContext context)
    {
        switch (context.ContextNode!.Kind)
        {
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.CallSignature:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.Constructor:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.InterfaceDeclaration: // This one is not truly a function, but for formatting purposes, it acts just like one
                return true;
        }

        return false;
    }

    internal static bool IsNotFunctionDeclContext(FormattingContext context) => !IsFunctionDeclContext(context);

    internal static bool IsFunctionDeclarationOrFunctionExpressionContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.FunctionDeclaration || context.ContextNode.Kind == SyntaxKind.FunctionExpression;

    internal static bool IsTypeScriptDeclWithBlockContext(FormattingContext context)
        => NodeIsTypeScriptDeclWithBlockContext(context.ContextNode!);

    internal static bool NodeIsTypeScriptDeclWithBlockContext(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.TypeLiteral:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.ExportDeclaration:
            case SyntaxKind.NamedExports:
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.NamedImports:
                return true;
        }

        return false;
    }

    internal static bool IsAfterCodeBlockContext(FormattingContext context)
    {
        switch (context.CurrentTokenParent!.Kind)
        {
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.CatchClause:
            case SyntaxKind.ModuleBlock:
            case SyntaxKind.SwitchStatement:
                return true;
            case SyntaxKind.Block:
                {
                    Node? blockParent = context.CurrentTokenParent.Parent;
                    // In a codefix scenario, we can't rely on parents being set. So just always return true.
                    if (blockParent is null || (blockParent.Kind != SyntaxKind.ArrowFunction && blockParent.Kind != SyntaxKind.FunctionExpression))
                    {
                        return true;
                    }
                    break;
                }
        }
        return false;
    }

    internal static bool IsControlDeclContext(FormattingContext context)
    {
        switch (context.ContextNode!.Kind)
        {
            case SyntaxKind.IfStatement:
            case SyntaxKind.SwitchStatement:
            case SyntaxKind.ForStatement:
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
            case SyntaxKind.WhileStatement:
            case SyntaxKind.TryStatement:
            case SyntaxKind.DoStatement:
            case SyntaxKind.WithStatement:
            case SyntaxKind.CatchClause:
                return true;

            default:
                return false;
        }
    }

    internal static bool IsObjectContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.ObjectLiteralExpression;

    internal static bool IsFunctionCallContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.CallExpression;

    internal static bool IsNewContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.NewExpression;

    internal static bool IsFunctionCallOrNewContext(FormattingContext context)
        => IsFunctionCallContext(context) || IsNewContext(context);

    internal static bool IsPreviousTokenNotComma(FormattingContext context)
        => context.CurrentTokenSpan.Kind != SyntaxKind.CommaToken;

    internal static bool IsNextTokenNotCloseBracket(FormattingContext context)
        => context.NextTokenSpan.Kind != SyntaxKind.CloseBracketToken;

    internal static bool IsNextTokenNotCloseParen(FormattingContext context)
        => context.NextTokenSpan.Kind != SyntaxKind.CloseParenToken;

    internal static bool IsArrowFunctionContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.ArrowFunction;

    internal static bool IsImportTypeContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.ImportType;

    internal static bool IsNonJsxSameLineTokenContext(FormattingContext context)
        => context.TokensAreOnSameLine() && context.ContextNode!.Kind != SyntaxKind.JsxText;

    internal static bool IsNonJsxTextContext(FormattingContext context)
        => context.ContextNode!.Kind != SyntaxKind.JsxText;

    internal static bool IsNonJsxElementOrFragmentContext(FormattingContext context)
        => context.ContextNode!.Kind != SyntaxKind.JsxElement && context.ContextNode.Kind != SyntaxKind.JsxFragment;

    internal static bool IsJsxExpressionContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.JsxExpression || context.ContextNode.Kind == SyntaxKind.JsxSpreadAttribute;

    internal static bool IsNextTokenParentJsxAttribute(FormattingContext context)
        => context.NextTokenParent!.Kind == SyntaxKind.JsxAttribute || (context.NextTokenParent.Kind == SyntaxKind.JsxNamespacedName && context.NextTokenParent.Parent!.Kind == SyntaxKind.JsxAttribute);

    internal static bool IsJsxAttributeContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.JsxAttribute;

    internal static bool IsNextTokenParentNotJsxNamespacedName(FormattingContext context)
        => context.NextTokenParent!.Kind != SyntaxKind.JsxNamespacedName;

    internal static bool IsNextTokenParentJsxNamespacedName(FormattingContext context)
        => context.NextTokenParent!.Kind == SyntaxKind.JsxNamespacedName;

    internal static bool IsJsxSelfClosingElementContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.JsxSelfClosingElement;

    internal static bool IsNotBeforeBlockInFunctionDeclarationContext(FormattingContext context)
        => !IsFunctionDeclContext(context) && !IsBeforeBlockContext(context);

    internal static bool IsEndOfDecoratorContextOnSameLine(FormattingContext context)
        => context.TokensAreOnSameLine() &&
           AstUtilities.HasDecorators(context.ContextNode!) &&
           NodeIsInDecoratorContext(context.CurrentTokenParent!) &&
           !NodeIsInDecoratorContext(context.NextTokenParent!);

    internal static bool NodeIsInDecoratorContext(Node? node)
    {
        while (node is not null && AstUtilities.IsExpression(node))
        {
            node = node.Parent;
        }
        return node is not null && node.Kind == SyntaxKind.Decorator;
    }

    internal static bool IsStartOfVariableDeclarationList(FormattingContext context)
        => context.CurrentTokenParent!.Kind == SyntaxKind.VariableDeclarationList &&
           global::Ts.Scanner.Scanner.GetTokenPosOfNode(context.CurrentTokenParent, context.SourceFile, false) == context.CurrentTokenSpan.Loc.Pos;

    internal static bool IsNotFormatOnEnter(FormattingContext context)
        => context.FormattingRequestKind != FormatRequestKind.FormatOnEnter;

    internal static bool IsModuleDeclContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.ModuleDeclaration;

    internal static bool IsObjectTypeContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.TypeLiteral;

    internal static bool IsConstructorSignatureContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.ConstructSignature;

    internal static bool IsTypeArgumentOrParameterOrAssertion(TextRangeWithKind token, Node parent)
    {
        if (token.Kind != SyntaxKind.LessThanToken && token.Kind != SyntaxKind.GreaterThanToken)
        {
            return false;
        }
        switch (parent.Kind)
        {
            case SyntaxKind.TypeReference:
            case SyntaxKind.TypeAssertionExpression:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.CallSignature:
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.CallExpression:
            case SyntaxKind.NewExpression:
            case SyntaxKind.ExpressionWithTypeArguments:
                return true;
            default:
                return false;
        }
    }

    internal static bool IsTypeArgumentOrParameterOrAssertionContext(FormattingContext context)
        => IsTypeArgumentOrParameterOrAssertion(context.CurrentTokenSpan, context.CurrentTokenParent!) ||
           IsTypeArgumentOrParameterOrAssertion(context.NextTokenSpan, context.NextTokenParent!);

    internal static bool IsTypeAssertionContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.TypeAssertionExpression;

    internal static bool IsNonTypeAssertionContext(FormattingContext context) => !IsTypeAssertionContext(context);

    internal static bool IsVoidOpContext(FormattingContext context)
        => context.CurrentTokenSpan.Kind == SyntaxKind.VoidKeyword && context.CurrentTokenParent!.Kind == SyntaxKind.VoidExpression;

    internal static bool IsYieldOrYieldStarWithOperand(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.YieldExpression && context.ContextNode.Expression() is not null;

    internal static bool IsNonNullAssertionContext(FormattingContext context)
        => context.ContextNode!.Kind == SyntaxKind.NonNullExpression;

    internal static bool IsNotStatementConditionContext(FormattingContext context) => !IsStatementConditionContext(context);

    internal static bool IsStatementConditionContext(FormattingContext context)
    {
        switch (context.ContextNode!.Kind)
        {
            case SyntaxKind.IfStatement:
            case SyntaxKind.ForStatement:
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
            case SyntaxKind.DoStatement:
            case SyntaxKind.WhileStatement:
                return true;

            default:
                return false;
        }
    }

    internal static bool IsSemicolonDeletionContext(FormattingContext context)
    {
        SyntaxKind nextTokenKind = context.NextTokenSpan.Kind;
        int nextTokenStart = context.NextTokenSpan.Loc.Pos;
        if (NodeTests.IsTriviaKind(nextTokenKind))
        {
            Node? nextRealToken;
            if (context.NextTokenParent == context.CurrentTokenParent)
            {
                // !!! TODO: very different from strada, but strada's logic here is wonky...
                nextRealToken = global::Ts.AstNav.Tokens.FindNextToken(context.NextTokenParent!, context.SourceFile.AsNode(), context.SourceFile);
            }
            else
            {
                nextRealToken = GetFirstToken(context.NextTokenParent!, context.SourceFile);
            }

            if (nextRealToken is null)
            {
                return true;
            }
            nextTokenKind = nextRealToken.Kind;
            nextTokenStart = global::Ts.Scanner.Scanner.GetTokenPosOfNode(nextRealToken, context.SourceFile, false);
        }

        int startLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(context.SourceFile, context.CurrentTokenSpan.Loc.Pos);
        int endLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(context.SourceFile, nextTokenStart);
        if (startLine == endLine)
        {
            return nextTokenKind == SyntaxKind.CloseBraceToken || nextTokenKind == SyntaxKind.EndOfFile;
        }

        if (nextTokenKind == SyntaxKind.SemicolonToken &&
            context.CurrentTokenSpan.Kind == SyntaxKind.SemicolonToken)
        {
            return true;
        }

        if (nextTokenKind == SyntaxKind.SemicolonClassElement ||
            nextTokenKind == SyntaxKind.SemicolonToken)
        {
            return false;
        }

        if (context.ContextNode!.Kind == SyntaxKind.InterfaceDeclaration ||
            context.ContextNode.Kind == SyntaxKind.TypeAliasDeclaration)
        {
            // Can't remove semicolon after `foo`; it would parse as a method declaration...
            return context.CurrentTokenParent!.Kind != SyntaxKind.PropertySignature ||
                   context.CurrentTokenParent.Type() is not null ||
                   nextTokenKind != SyntaxKind.OpenParenToken;
        }

        if (NodeTests.IsPropertyDeclaration(context.CurrentTokenParent!))
        {
            return context.CurrentTokenParent!.Initializer() is null;
        }

        return context.CurrentTokenParent!.Kind != SyntaxKind.ForStatement &&
               context.CurrentTokenParent.Kind != SyntaxKind.EmptyStatement &&
               context.CurrentTokenParent.Kind != SyntaxKind.SemicolonClassElement &&
               nextTokenKind != SyntaxKind.OpenBracketToken &&
               nextTokenKind != SyntaxKind.OpenParenToken &&
               nextTokenKind != SyntaxKind.PlusToken &&
               nextTokenKind != SyntaxKind.MinusToken &&
               nextTokenKind != SyntaxKind.SlashToken &&
               nextTokenKind != SyntaxKind.RegularExpressionLiteral &&
               nextTokenKind != SyntaxKind.CommaToken &&
               nextTokenKind != SyntaxKind.TemplateExpression &&
               nextTokenKind != SyntaxKind.TemplateHead &&
               nextTokenKind != SyntaxKind.NoSubstitutionTemplateLiteral &&
               nextTokenKind != SyntaxKind.DotToken;
    }

    internal static bool IsSemicolonInsertionContext(FormattingContext context)
        => PositionIsASICandidate(context.CurrentTokenSpan.Loc.End, context.CurrentTokenParent!, context.SourceFile);

    internal static bool IsNotPropertyAccessOnIntegerLiteral(FormattingContext context)
    {
        // Go short-circuits: PropertyAccessExpression guarantees Expression() is non-null.
        Node contextNode = context.ContextNode!;
        return !NodeTests.IsPropertyAccessExpression(contextNode) ||
               !NodeTests.IsNumericLiteral(contextNode.Expression()!) ||
               contextNode.Expression()!.Text().Contains('.', StringComparison.Ordinal);
    }

    // PORT-NOTE: lsutil.GetFirstToken and lsutil.PositionIsASICandidate are not yet
    // ported (the ls/lsutil package is scheduled before format in plan/06 §13.2 but
    // is not present yet). These two functions are referenced ONLY by the semicolon
    // deletion/insertion predicates. They are stubbed to their conservative Go-visible
    // fallback until lsutil lands, and flagged in the dispatch report for wiring up.
    //
    // GetFirstToken(node, sourceFile): the real impl returns the first token child of
    // `node`. Returning null here makes isSemicolonDeletionContext take the "return
    // true" branch (Go: `if nextRealToken == nil { return true }`) — a documented,
    // over-eager stub that only affects semicolon-normalization rules (opt-in via
    // Semicolons preference), not default formatting.
    private static Node? GetFirstToken(Node node, SourceFile sourceFile)
    {
        // !!! STUB: awaiting lsutil.GetFirstToken port.
        return null;
    }

    // PositionIsASICandidate(pos, parent, sourceFile): the real impl tests whether an
    // automatic-semicolon-insertion point exists at pos. Stubbed to false so
    // isSemicolonInsertionContext never fires until lsutil.PositionIsASICandidate is
    // ported; again only affects the opt-in Semicolons=Insert rules.
    private static bool PositionIsASICandidate(int pos, Node parent, SourceFile sourceFile)
    {
        // !!! STUB: awaiting lsutil.PositionIsASICandidate port.
        return false;
    }
}
