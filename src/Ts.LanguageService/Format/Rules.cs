// Ported from tsgo internal/format/rules.go
//
// CRITICAL (plan/06 §8.2): the ORDER of rules in getAllRules() is load-bearing
// (it feeds RulesMap.AddRule's sub-bucket insertion and thus rule priority). The
// three sub-lists and every rule within them are reproduced in exact source order.

using System.Collections.Generic;
using Ts.Ast;
using RC = Ts.Format.RuleContext;

namespace Ts.Format;

internal static partial class Rules
{
    // Go: func getAllRules() []ruleSpec
    internal static List<RuleSpec> GetAllRules()
    {
        // Go: allTokens := make(...); loop FirstToken..LastToken excluding EndOfFile.
        var allTokensList = new List<SyntaxKind>();
        for (int token = (int)SyntaxKind.FirstToken; token <= (int)SyntaxKind.LastToken; token++)
        {
            if ((SyntaxKind)token != SyntaxKind.EndOfFile)
            {
                allTokensList.Add((SyntaxKind)token);
            }
        }
        SyntaxKind[] allTokens = allTokensList.ToArray();

        // Go: anyTokenExcept := func(tokens ...ast.Kind) tokenRange { ... }
        TokenRange AnyTokenExcept(params SyntaxKind[] tokens)
        {
            var newTokens = new List<SyntaxKind>(allTokens.Length);
            foreach (var token in allTokens)
            {
                if (System.Array.IndexOf(tokens, token) >= 0)
                {
                    continue;
                }
                newTokens.Add(token);
            }
            return new TokenRange { IsSpecific = false, Tokens = newTokens.ToArray() };
        }

        var anyToken = new TokenRange { IsSpecific = false, Tokens = allTokens };

        var anyTokenIncludingMultilineComments = TokenRangeFromEx(allTokens, SyntaxKind.MultiLineCommentTrivia);
        var anyTokenIncludingEof = TokenRangeFromEx(allTokens, SyntaxKind.EndOfFile);
        var keywords = TokenRangeFromRange(SyntaxKind.FirstKeyword, SyntaxKind.LastKeyword);
        var binaryOperators = TokenRangeFromRange(SyntaxKind.FirstBinaryOperator, SyntaxKind.LastBinaryOperator);
        var binaryKeywordOperators = new[]
        {
            SyntaxKind.InKeyword,
            SyntaxKind.InstanceOfKeyword,
            SyntaxKind.OfKeyword,
            SyntaxKind.AsKeyword,
            SyntaxKind.IsKeyword,
            SyntaxKind.SatisfiesKeyword,
        };
        var unaryPrefixOperators = new[] { SyntaxKind.PlusPlusToken, SyntaxKind.MinusToken, SyntaxKind.TildeToken, SyntaxKind.ExclamationToken };
        var unaryPrefixExpressions = new[]
        {
            SyntaxKind.NumericLiteral,
            SyntaxKind.BigIntLiteral,
            SyntaxKind.Identifier,
            SyntaxKind.OpenParenToken,
            SyntaxKind.OpenBracketToken,
            SyntaxKind.OpenBraceToken,
            SyntaxKind.ThisKeyword,
            SyntaxKind.NewKeyword,
        };
        var unaryPreincrementExpressions = new[] { SyntaxKind.Identifier, SyntaxKind.OpenParenToken, SyntaxKind.ThisKeyword, SyntaxKind.NewKeyword };
        var unaryPostincrementExpressions = new[] { SyntaxKind.Identifier, SyntaxKind.CloseParenToken, SyntaxKind.CloseBracketToken, SyntaxKind.NewKeyword };
        var unaryPredecrementExpressions = new[] { SyntaxKind.Identifier, SyntaxKind.OpenParenToken, SyntaxKind.ThisKeyword, SyntaxKind.NewKeyword };
        var unaryPostdecrementExpressions = new[] { SyntaxKind.Identifier, SyntaxKind.CloseParenToken, SyntaxKind.CloseBracketToken, SyntaxKind.NewKeyword };
        var comments = new[] { SyntaxKind.SingleLineCommentTrivia, SyntaxKind.MultiLineCommentTrivia };
        var typeKeywords = new[]
        {
            SyntaxKind.AnyKeyword,
            SyntaxKind.AssertsKeyword,
            SyntaxKind.BigIntKeyword,
            SyntaxKind.BooleanKeyword,
            SyntaxKind.FalseKeyword,
            SyntaxKind.InferKeyword,
            SyntaxKind.KeyOfKeyword,
            SyntaxKind.NeverKeyword,
            SyntaxKind.NullKeyword,
            SyntaxKind.NumberKeyword,
            SyntaxKind.ObjectKeyword,
            SyntaxKind.ReadonlyKeyword,
            SyntaxKind.StringKeyword,
            SyntaxKind.SymbolKeyword,
            SyntaxKind.TypeOfKeyword,
            SyntaxKind.TrueKeyword,
            SyntaxKind.VoidKeyword,
            SyntaxKind.UndefinedKeyword,
            SyntaxKind.UniqueKeyword,
            SyntaxKind.UnknownKeyword,
        };
        // Go: typeNames := append([]ast.Kind{ast.KindIdentifier}, typeKeywords...)
        var typeNamesList = new List<SyntaxKind> { SyntaxKind.Identifier };
        typeNamesList.AddRange(typeKeywords);
        SyntaxKind[] typeNames = typeNamesList.ToArray();

        // Place a space before open brace in a function declaration
        var functionOpenBraceLeftTokenRange = anyTokenIncludingMultilineComments;

        // Place a space before open brace in a TypeScript declaration that has braces as children (class, module, enum, etc)
        var typeScriptOpenBraceLeftTokenRange = TokenRangeFrom(SyntaxKind.Identifier, SyntaxKind.GreaterThanToken, SyntaxKind.MultiLineCommentTrivia, SyntaxKind.ClassKeyword, SyntaxKind.ExportKeyword, SyntaxKind.ImportKeyword);

        // Place a space before open brace in a control flow construct
        var controlOpenBraceLeftTokenRange = TokenRangeFrom(SyntaxKind.CloseParenToken, SyntaxKind.MultiLineCommentTrivia, SyntaxKind.DoKeyword, SyntaxKind.TryKeyword, SyntaxKind.FinallyKeyword, SyntaxKind.ElseKeyword, SyntaxKind.CatchKeyword);

        // These rules are higher in priority than user-configurable
        var highPriorityCommonRules = new List<RuleSpec>
        {
            // Leave comments alone
            Rule("IgnoreBeforeComment", anyToken, comments, AnyContext, RuleAction.StopProcessingSpaceActions),
            Rule("IgnoreAfterLineComment", SyntaxKind.SingleLineCommentTrivia, anyToken, AnyContext, RuleAction.StopProcessingSpaceActions),

            Rule("NotSpaceBeforeColon", anyToken, SyntaxKind.ColonToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsNotBinaryOpContext, RC.IsNotTypeAnnotationContext }, RuleAction.DeleteSpace),
            Rule("SpaceAfterColon", SyntaxKind.ColonToken, anyToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsNotBinaryOpContext, RC.IsNextTokenParentNotJsxNamespacedName }, RuleAction.InsertSpace),
            Rule("NoSpaceBeforeQuestionMark", anyToken, SyntaxKind.QuestionToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsNotBinaryOpContext, RC.IsNotTypeAnnotationContext }, RuleAction.DeleteSpace),
            // insert space after '?' only when it is used in conditional operator
            Rule("SpaceAfterQuestionMarkInConditionalOperator", SyntaxKind.QuestionToken, anyToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsConditionalOperatorContext }, RuleAction.InsertSpace),

            // in other cases there should be no space between '?' and next token
            Rule("NoSpaceAfterQuestionMark", SyntaxKind.QuestionToken, anyToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsNonOptionalPropertyContext }, RuleAction.DeleteSpace),

            Rule("NoSpaceBeforeDot", anyToken, new[] { SyntaxKind.DotToken, SyntaxKind.QuestionDotToken }, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsNotPropertyAccessOnIntegerLiteral }, RuleAction.DeleteSpace),
            Rule("NoSpaceAfterDot", new[] { SyntaxKind.DotToken, SyntaxKind.QuestionDotToken }, anyToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),

            Rule("NoSpaceBetweenImportParenInImportType", SyntaxKind.ImportKeyword, SyntaxKind.OpenParenToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsImportTypeContext }, RuleAction.DeleteSpace),

            // Special handling of unary operators.
            Rule("NoSpaceAfterUnaryPrefixOperator", unaryPrefixOperators, unaryPrefixExpressions, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsNotBinaryOpContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceAfterUnaryPreincrementOperator", SyntaxKind.PlusPlusToken, unaryPreincrementExpressions, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceAfterUnaryPredecrementOperator", SyntaxKind.MinusMinusToken, unaryPredecrementExpressions, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceBeforeUnaryPostincrementOperator", unaryPostincrementExpressions, SyntaxKind.PlusPlusToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsNotStatementConditionContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceBeforeUnaryPostdecrementOperator", unaryPostdecrementExpressions, SyntaxKind.MinusMinusToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsNotStatementConditionContext }, RuleAction.DeleteSpace),

            // More unary operator special-casing.
            Rule("SpaceAfterPostincrementWhenFollowedByAdd", SyntaxKind.PlusPlusToken, SyntaxKind.PlusToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsBinaryOpContext }, RuleAction.InsertSpace),
            Rule("SpaceAfterAddWhenFollowedByUnaryPlus", SyntaxKind.PlusToken, SyntaxKind.PlusToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsBinaryOpContext }, RuleAction.InsertSpace),
            Rule("SpaceAfterAddWhenFollowedByPreincrement", SyntaxKind.PlusToken, SyntaxKind.PlusPlusToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsBinaryOpContext }, RuleAction.InsertSpace),
            Rule("SpaceAfterPostdecrementWhenFollowedBySubtract", SyntaxKind.MinusMinusToken, SyntaxKind.MinusToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsBinaryOpContext }, RuleAction.InsertSpace),
            Rule("SpaceAfterSubtractWhenFollowedByUnaryMinus", SyntaxKind.MinusToken, SyntaxKind.MinusToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsBinaryOpContext }, RuleAction.InsertSpace),
            Rule("SpaceAfterSubtractWhenFollowedByPredecrement", SyntaxKind.MinusToken, SyntaxKind.MinusMinusToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsBinaryOpContext }, RuleAction.InsertSpace),

            Rule("NoSpaceAfterCloseBrace", SyntaxKind.CloseBraceToken, new[] { SyntaxKind.CommaToken, SyntaxKind.SemicolonToken }, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            // For functions and control block place } on a new line multi-line rule
            Rule("NewLineBeforeCloseBraceInBlockContext", anyTokenIncludingMultilineComments, SyntaxKind.CloseBraceToken, new List<ContextPredicate> { RC.IsMultilineBlockContext }, RuleAction.InsertNewLine),

            // Space/new line after }.
            Rule("SpaceAfterCloseBrace", SyntaxKind.CloseBraceToken, AnyTokenExcept(SyntaxKind.CloseParenToken), new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsAfterCodeBlockContext }, RuleAction.InsertSpace),
            // Special case for (}, else) and (}, while)
            Rule("SpaceBetweenCloseBraceAndElse", SyntaxKind.CloseBraceToken, SyntaxKind.ElseKeyword, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("SpaceBetweenCloseBraceAndWhile", SyntaxKind.CloseBraceToken, SyntaxKind.WhileKeyword, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("NoSpaceBetweenEmptyBraceBrackets", SyntaxKind.OpenBraceToken, SyntaxKind.CloseBraceToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsObjectContext }, RuleAction.DeleteSpace),

            // Add a space after control dec context if the next character is an open bracket
            Rule("SpaceAfterConditionalClosingParen", SyntaxKind.CloseParenToken, SyntaxKind.OpenBracketToken, new List<ContextPredicate> { RC.IsControlDeclContext }, RuleAction.InsertSpace),

            Rule("NoSpaceBetweenFunctionKeywordAndStar", SyntaxKind.FunctionKeyword, SyntaxKind.AsteriskToken, new List<ContextPredicate> { RC.IsFunctionDeclarationOrFunctionExpressionContext }, RuleAction.DeleteSpace),
            Rule("SpaceAfterStarInGeneratorDeclaration", SyntaxKind.AsteriskToken, SyntaxKind.Identifier, new List<ContextPredicate> { RC.IsFunctionDeclarationOrFunctionExpressionContext }, RuleAction.InsertSpace),

            Rule("SpaceAfterFunctionInFuncDecl", SyntaxKind.FunctionKeyword, anyToken, new List<ContextPredicate> { RC.IsFunctionDeclContext }, RuleAction.InsertSpace),
            // Insert new line after { and before } in multi-line contexts.
            Rule("NewLineAfterOpenBraceInBlockContext", SyntaxKind.OpenBraceToken, anyToken, new List<ContextPredicate> { RC.IsMultilineBlockContext }, RuleAction.InsertNewLine),

            // For get/set members
            Rule("SpaceAfterGetSetInMember", new[] { SyntaxKind.GetKeyword, SyntaxKind.SetKeyword }, SyntaxKind.Identifier, new List<ContextPredicate> { RC.IsFunctionDeclContext }, RuleAction.InsertSpace),

            Rule("NoSpaceBetweenYieldKeywordAndStar", SyntaxKind.YieldKeyword, SyntaxKind.AsteriskToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsYieldOrYieldStarWithOperand }, RuleAction.DeleteSpace),
            Rule("SpaceBetweenYieldOrYieldStarAndOperand", new[] { SyntaxKind.YieldKeyword, SyntaxKind.AsteriskToken }, anyToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsYieldOrYieldStarWithOperand }, RuleAction.InsertSpace),

            Rule("NoSpaceBetweenReturnAndSemicolon", SyntaxKind.ReturnKeyword, SyntaxKind.SemicolonToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            Rule("SpaceAfterCertainKeywords", new[] { SyntaxKind.VarKeyword, SyntaxKind.ThrowKeyword, SyntaxKind.NewKeyword, SyntaxKind.DeleteKeyword, SyntaxKind.ReturnKeyword, SyntaxKind.TypeOfKeyword, SyntaxKind.AwaitKeyword }, anyToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("SpaceAfterLetConstInVariableDeclaration", new[] { SyntaxKind.LetKeyword, SyntaxKind.ConstKeyword }, anyToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsStartOfVariableDeclarationList }, RuleAction.InsertSpace),
            Rule("NoSpaceBeforeOpenParenInFuncCall", anyToken, SyntaxKind.OpenParenToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsFunctionCallOrNewContext, RC.IsPreviousTokenNotComma }, RuleAction.DeleteSpace),

            // Special case for binary operators (that are keywords).
            Rule("SpaceBeforeBinaryKeywordOperator", anyToken, binaryKeywordOperators, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsBinaryOpContext }, RuleAction.InsertSpace),
            Rule("SpaceAfterBinaryKeywordOperator", binaryKeywordOperators, anyToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsBinaryOpContext }, RuleAction.InsertSpace),

            Rule("SpaceAfterVoidOperator", SyntaxKind.VoidKeyword, anyToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsVoidOpContext }, RuleAction.InsertSpace),

            // Async-await
            Rule("SpaceBetweenAsyncAndOpenParen", SyntaxKind.AsyncKeyword, SyntaxKind.OpenParenToken, new List<ContextPredicate> { RC.IsArrowFunctionContext, RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("SpaceBetweenAsyncAndFunctionKeyword", SyntaxKind.AsyncKeyword, new[] { SyntaxKind.FunctionKeyword, SyntaxKind.Identifier }, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),

            // Template string
            Rule("NoSpaceBetweenTagAndTemplateString", new[] { SyntaxKind.Identifier, SyntaxKind.CloseParenToken }, new[] { SyntaxKind.NoSubstitutionTemplateLiteral, SyntaxKind.TemplateHead }, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),

            // JSX opening elements
            Rule("SpaceBeforeJsxAttribute", anyToken, SyntaxKind.Identifier, new List<ContextPredicate> { RC.IsNextTokenParentJsxAttribute, RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("SpaceBeforeSlashInJsxOpeningElement", anyToken, SyntaxKind.SlashToken, new List<ContextPredicate> { RC.IsJsxSelfClosingElementContext, RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("NoSpaceBeforeGreaterThanTokenInJsxOpeningElement", SyntaxKind.SlashToken, SyntaxKind.GreaterThanToken, new List<ContextPredicate> { RC.IsJsxSelfClosingElementContext, RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceBeforeEqualInJsxAttribute", anyToken, SyntaxKind.EqualsToken, new List<ContextPredicate> { RC.IsJsxAttributeContext, RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceAfterEqualInJsxAttribute", SyntaxKind.EqualsToken, anyToken, new List<ContextPredicate> { RC.IsJsxAttributeContext, RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceBeforeJsxNamespaceColon", SyntaxKind.Identifier, SyntaxKind.ColonToken, new List<ContextPredicate> { RC.IsNextTokenParentJsxNamespacedName }, RuleAction.DeleteSpace),
            Rule("NoSpaceAfterJsxNamespaceColon", SyntaxKind.ColonToken, SyntaxKind.Identifier, new List<ContextPredicate> { RC.IsNextTokenParentJsxNamespacedName }, RuleAction.DeleteSpace),

            // TypeScript-specific rules
            Rule("NoSpaceAfterModuleImport", new[] { SyntaxKind.ModuleKeyword, SyntaxKind.RequireKeyword }, SyntaxKind.OpenParenToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            // Add a space around certain TypeScript keywords
            Rule(
                "SpaceAfterCertainTypeScriptKeywords",
                new[]
                {
                    SyntaxKind.AbstractKeyword,
                    SyntaxKind.AccessorKeyword,
                    SyntaxKind.ClassKeyword,
                    SyntaxKind.DeclareKeyword,
                    SyntaxKind.DefaultKeyword,
                    SyntaxKind.EnumKeyword,
                    SyntaxKind.ExportKeyword,
                    SyntaxKind.ExtendsKeyword,
                    SyntaxKind.GetKeyword,
                    SyntaxKind.ImplementsKeyword,
                    SyntaxKind.ImportKeyword,
                    SyntaxKind.InterfaceKeyword,
                    SyntaxKind.ModuleKeyword,
                    SyntaxKind.NamespaceKeyword,
                    SyntaxKind.OverrideKeyword,
                    SyntaxKind.PrivateKeyword,
                    SyntaxKind.PublicKeyword,
                    SyntaxKind.ProtectedKeyword,
                    SyntaxKind.ReadonlyKeyword,
                    SyntaxKind.SetKeyword,
                    SyntaxKind.StaticKeyword,
                    SyntaxKind.TypeKeyword,
                    SyntaxKind.FromKeyword,
                    SyntaxKind.KeyOfKeyword,
                    SyntaxKind.InferKeyword,
                },
                anyToken,
                new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext },
                RuleAction.InsertSpace),
            Rule(
                "SpaceBeforeCertainTypeScriptKeywords",
                anyToken,
                new[] { SyntaxKind.ExtendsKeyword, SyntaxKind.ImplementsKeyword, SyntaxKind.FromKeyword },
                new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext },
                RuleAction.InsertSpace),
            // Treat string literals in module names as identifiers
            Rule("SpaceAfterModuleName", SyntaxKind.StringLiteral, SyntaxKind.OpenBraceToken, new List<ContextPredicate> { RC.IsModuleDeclContext }, RuleAction.InsertSpace),

            // Lambda expressions
            Rule("SpaceBeforeArrow", anyToken, SyntaxKind.EqualsGreaterThanToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("SpaceAfterArrow", SyntaxKind.EqualsGreaterThanToken, anyToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),

            // Optional parameters and let args
            Rule("NoSpaceAfterEllipsis", SyntaxKind.DotDotDotToken, SyntaxKind.Identifier, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceAfterOptionalParameters", SyntaxKind.QuestionToken, new[] { SyntaxKind.CloseParenToken, SyntaxKind.CommaToken }, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsNotBinaryOpContext }, RuleAction.DeleteSpace),

            // Remove spaces in empty interface literals. e.g.: x: {}
            Rule("NoSpaceBetweenEmptyInterfaceBraceBrackets", SyntaxKind.OpenBraceToken, SyntaxKind.CloseBraceToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsObjectTypeContext }, RuleAction.DeleteSpace),

            // generics and type assertions
            Rule("NoSpaceBeforeOpenAngularBracket", typeNames, SyntaxKind.LessThanToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsTypeArgumentOrParameterOrAssertionContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceBetweenCloseParenAndAngularBracket", SyntaxKind.CloseParenToken, SyntaxKind.LessThanToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsTypeArgumentOrParameterOrAssertionContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceAfterOpenAngularBracket", SyntaxKind.LessThanToken, anyToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsTypeArgumentOrParameterOrAssertionContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceBeforeCloseAngularBracket", anyToken, SyntaxKind.GreaterThanToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsTypeArgumentOrParameterOrAssertionContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceAfterCloseAngularBracket", SyntaxKind.GreaterThanToken, new[] { SyntaxKind.OpenParenToken, SyntaxKind.OpenBracketToken, SyntaxKind.GreaterThanToken, SyntaxKind.CommaToken }, new List<ContextPredicate>
            {
                RC.IsNonJsxSameLineTokenContext,
                RC.IsTypeArgumentOrParameterOrAssertionContext,
                RC.IsNotFunctionDeclContext, /*To prevent an interference with the SpaceBeforeOpenParenInFuncDecl rule*/
                RC.IsNonTypeAssertionContext,
            }, RuleAction.DeleteSpace),

            // decorators
            Rule("SpaceBeforeAt", new[] { SyntaxKind.CloseParenToken, SyntaxKind.Identifier }, SyntaxKind.AtToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("NoSpaceAfterAt", SyntaxKind.AtToken, anyToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            // Insert space after @ in decorator
            Rule(
                "SpaceAfterDecorator",
                anyToken,
                new[]
                {
                    SyntaxKind.AbstractKeyword,
                    SyntaxKind.Identifier,
                    SyntaxKind.ExportKeyword,
                    SyntaxKind.DefaultKeyword,
                    SyntaxKind.ClassKeyword,
                    SyntaxKind.StaticKeyword,
                    SyntaxKind.PublicKeyword,
                    SyntaxKind.PrivateKeyword,
                    SyntaxKind.ProtectedKeyword,
                    SyntaxKind.GetKeyword,
                    SyntaxKind.SetKeyword,
                    SyntaxKind.OpenBracketToken,
                    SyntaxKind.AsteriskToken,
                },
                new List<ContextPredicate> { RC.IsEndOfDecoratorContextOnSameLine },
                RuleAction.InsertSpace),

            Rule("NoSpaceBeforeNonNullAssertionOperator", anyToken, SyntaxKind.ExclamationToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsNonNullAssertionContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceAfterNewKeywordOnConstructorSignature", SyntaxKind.NewKeyword, SyntaxKind.OpenParenToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsConstructorSignatureContext }, RuleAction.DeleteSpace),
            Rule("SpaceLessThanAndNonJSXTypeAnnotation", SyntaxKind.LessThanToken, SyntaxKind.LessThanToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
        };

        // These rules are applied after high priority
        var userConfigurableRules = new List<RuleSpec>
        {
            // Treat constructor as an identifier in a function declaration
            Rule("SpaceAfterConstructor", SyntaxKind.ConstructorKeyword, SyntaxKind.OpenParenToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterConstructorOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("NoSpaceAfterConstructor", SyntaxKind.ConstructorKeyword, SyntaxKind.OpenParenToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceAfterConstructorOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),

            Rule("SpaceAfterComma", SyntaxKind.CommaToken, anyToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterCommaDelimiterOption), RC.IsNonJsxSameLineTokenContext, RC.IsNonJsxElementOrFragmentContext, RC.IsNextTokenNotCloseBracket, RC.IsNextTokenNotCloseParen }, RuleAction.InsertSpace),
            Rule("NoSpaceAfterComma", SyntaxKind.CommaToken, anyToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceAfterCommaDelimiterOption), RC.IsNonJsxSameLineTokenContext, RC.IsNonJsxElementOrFragmentContext }, RuleAction.DeleteSpace),

            // Insert space after function keyword for anonymous functions
            Rule("SpaceAfterAnonymousFunctionKeyword", new[] { SyntaxKind.FunctionKeyword, SyntaxKind.AsteriskToken }, SyntaxKind.OpenParenToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterFunctionKeywordForAnonymousFunctionsOption), RC.IsFunctionDeclContext }, RuleAction.InsertSpace),
            Rule("NoSpaceAfterAnonymousFunctionKeyword", new[] { SyntaxKind.FunctionKeyword, SyntaxKind.AsteriskToken }, SyntaxKind.OpenParenToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceAfterFunctionKeywordForAnonymousFunctionsOption), RC.IsFunctionDeclContext }, RuleAction.DeleteSpace),

            // Insert space after keywords in control flow statements
            Rule("SpaceAfterKeywordInControl", keywords, SyntaxKind.OpenParenToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterKeywordsInControlFlowStatementsOption), RC.IsControlDeclContext }, RuleAction.InsertSpace),
            Rule("NoSpaceAfterKeywordInControl", keywords, SyntaxKind.OpenParenToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceAfterKeywordsInControlFlowStatementsOption), RC.IsControlDeclContext }, RuleAction.DeleteSpace),

            // Insert space after opening and before closing nonempty parenthesis
            Rule("SpaceAfterOpenParen", SyntaxKind.OpenParenToken, anyToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesisOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("SpaceBeforeCloseParen", anyToken, SyntaxKind.CloseParenToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesisOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("SpaceBetweenOpenParens", SyntaxKind.OpenParenToken, SyntaxKind.OpenParenToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesisOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("NoSpaceBetweenParens", SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceAfterOpenParen", SyntaxKind.OpenParenToken, anyToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesisOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceBeforeCloseParen", anyToken, SyntaxKind.CloseParenToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesisOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),

            // Insert space after opening and before closing nonempty brackets
            Rule("SpaceAfterOpenBracket", SyntaxKind.OpenBracketToken, anyToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBracketsOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("SpaceBeforeCloseBracket", anyToken, SyntaxKind.CloseBracketToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBracketsOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("NoSpaceBetweenBrackets", SyntaxKind.OpenBracketToken, SyntaxKind.CloseBracketToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceAfterOpenBracket", SyntaxKind.OpenBracketToken, anyToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBracketsOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceBeforeCloseBracket", anyToken, SyntaxKind.CloseBracketToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBracketsOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),

            // Insert a space after { and before } in single-line contexts
            Rule("SpaceAfterOpenBrace", SyntaxKind.OpenBraceToken, anyToken, new List<ContextPredicate> { RC.IsOptionEnabledOrUndefined(RC.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBracesOption), RC.IsBraceWrappedContext }, RuleAction.InsertSpace),
            Rule("SpaceBeforeCloseBrace", anyToken, SyntaxKind.CloseBraceToken, new List<ContextPredicate> { RC.IsOptionEnabledOrUndefined(RC.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBracesOption), RC.IsBraceWrappedContext }, RuleAction.InsertSpace),
            Rule("NoSpaceBetweenEmptyBraceBrackets", SyntaxKind.OpenBraceToken, SyntaxKind.CloseBraceToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsObjectContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceAfterOpenBrace", SyntaxKind.OpenBraceToken, anyToken, new List<ContextPredicate> { RC.IsOptionDisabled(RC.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBracesOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceBeforeCloseBrace", anyToken, SyntaxKind.CloseBraceToken, new List<ContextPredicate> { RC.IsOptionDisabled(RC.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBracesOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),

            // Insert a space after opening and before closing empty brace brackets
            Rule("SpaceBetweenEmptyBraceBrackets", SyntaxKind.OpenBraceToken, SyntaxKind.CloseBraceToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterOpeningAndBeforeClosingEmptyBracesOption) }, RuleAction.InsertSpace),
            Rule("NoSpaceBetweenEmptyBraceBrackets", SyntaxKind.OpenBraceToken, SyntaxKind.CloseBraceToken, new List<ContextPredicate> { RC.IsOptionDisabled(RC.InsertSpaceAfterOpeningAndBeforeClosingEmptyBracesOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),

            // Insert space after opening and before closing template string braces
            Rule("SpaceAfterTemplateHeadAndMiddle", new[] { SyntaxKind.TemplateHead, SyntaxKind.TemplateMiddle }, anyToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterOpeningAndBeforeClosingTemplateStringBracesOption), RC.IsNonJsxTextContext }, RuleAction.InsertSpace, RuleFlags.CanDeleteNewLines),
            Rule("SpaceBeforeTemplateMiddleAndTail", anyToken, new[] { SyntaxKind.TemplateMiddle, SyntaxKind.TemplateTail }, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterOpeningAndBeforeClosingTemplateStringBracesOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
            Rule("NoSpaceAfterTemplateHeadAndMiddle", new[] { SyntaxKind.TemplateHead, SyntaxKind.TemplateMiddle }, anyToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceAfterOpeningAndBeforeClosingTemplateStringBracesOption), RC.IsNonJsxTextContext }, RuleAction.DeleteSpace, RuleFlags.CanDeleteNewLines),
            Rule("NoSpaceBeforeTemplateMiddleAndTail", anyToken, new[] { SyntaxKind.TemplateMiddle, SyntaxKind.TemplateTail }, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceAfterOpeningAndBeforeClosingTemplateStringBracesOption), RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),

            // No space after { and before } in JSX expression
            Rule("SpaceAfterOpenBraceInJsxExpression", SyntaxKind.OpenBraceToken, anyToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterOpeningAndBeforeClosingJsxExpressionBracesOption), RC.IsNonJsxSameLineTokenContext, RC.IsJsxExpressionContext }, RuleAction.InsertSpace),
            Rule("SpaceBeforeCloseBraceInJsxExpression", anyToken, SyntaxKind.CloseBraceToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterOpeningAndBeforeClosingJsxExpressionBracesOption), RC.IsNonJsxSameLineTokenContext, RC.IsJsxExpressionContext }, RuleAction.InsertSpace),
            Rule("NoSpaceAfterOpenBraceInJsxExpression", SyntaxKind.OpenBraceToken, anyToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceAfterOpeningAndBeforeClosingJsxExpressionBracesOption), RC.IsNonJsxSameLineTokenContext, RC.IsJsxExpressionContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceBeforeCloseBraceInJsxExpression", anyToken, SyntaxKind.CloseBraceToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceAfterOpeningAndBeforeClosingJsxExpressionBracesOption), RC.IsNonJsxSameLineTokenContext, RC.IsJsxExpressionContext }, RuleAction.DeleteSpace),

            // Insert space after semicolon in for statement
            Rule("SpaceAfterSemicolonInFor", SyntaxKind.SemicolonToken, anyToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterSemicolonInForStatementsOption), RC.IsNonJsxSameLineTokenContext, RC.IsForContext }, RuleAction.InsertSpace),
            Rule("NoSpaceAfterSemicolonInFor", SyntaxKind.SemicolonToken, anyToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceAfterSemicolonInForStatementsOption), RC.IsNonJsxSameLineTokenContext, RC.IsForContext }, RuleAction.DeleteSpace),

            // Insert space before and after binary operators
            Rule("SpaceBeforeBinaryOperator", anyToken, binaryOperators, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceBeforeAndAfterBinaryOperatorsOption), RC.IsNonJsxSameLineTokenContext, RC.IsBinaryOpContext }, RuleAction.InsertSpace),
            Rule("SpaceAfterBinaryOperator", binaryOperators, anyToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceBeforeAndAfterBinaryOperatorsOption), RC.IsNonJsxSameLineTokenContext, RC.IsBinaryOpContext }, RuleAction.InsertSpace),
            Rule("NoSpaceBeforeBinaryOperator", anyToken, binaryOperators, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceBeforeAndAfterBinaryOperatorsOption), RC.IsNonJsxSameLineTokenContext, RC.IsBinaryOpContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceAfterBinaryOperator", binaryOperators, anyToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceBeforeAndAfterBinaryOperatorsOption), RC.IsNonJsxSameLineTokenContext, RC.IsBinaryOpContext }, RuleAction.DeleteSpace),

            Rule("SpaceBeforeOpenParenInFuncDecl", anyToken, SyntaxKind.OpenParenToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceBeforeFunctionParenthesisOption), RC.IsNonJsxSameLineTokenContext, RC.IsFunctionDeclContext }, RuleAction.InsertSpace),
            Rule("NoSpaceBeforeOpenParenInFuncDecl", anyToken, SyntaxKind.OpenParenToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceBeforeFunctionParenthesisOption), RC.IsNonJsxSameLineTokenContext, RC.IsFunctionDeclContext }, RuleAction.DeleteSpace),

            // Open Brace braces after control block
            Rule("NewLineBeforeOpenBraceInControl", controlOpenBraceLeftTokenRange, SyntaxKind.OpenBraceToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.PlaceOpenBraceOnNewLineForControlBlocksOption), RC.IsControlDeclContext, RC.IsBeforeMultilineBlockContext }, RuleAction.InsertNewLine, RuleFlags.CanDeleteNewLines),

            // Open Brace braces after function
            Rule("NewLineBeforeOpenBraceInFunction", functionOpenBraceLeftTokenRange, SyntaxKind.OpenBraceToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.PlaceOpenBraceOnNewLineForFunctionsOption), RC.IsFunctionDeclContext, RC.IsBeforeMultilineBlockContext }, RuleAction.InsertNewLine, RuleFlags.CanDeleteNewLines),
            // Open Brace braces after TypeScript module/class/interface
            Rule("NewLineBeforeOpenBraceInTypeScriptDeclWithBlock", typeScriptOpenBraceLeftTokenRange, SyntaxKind.OpenBraceToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.PlaceOpenBraceOnNewLineForFunctionsOption), RC.IsTypeScriptDeclWithBlockContext, RC.IsBeforeMultilineBlockContext }, RuleAction.InsertNewLine, RuleFlags.CanDeleteNewLines),

            Rule("SpaceAfterTypeAssertion", SyntaxKind.GreaterThanToken, anyToken, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceAfterTypeAssertionOption), RC.IsNonJsxSameLineTokenContext, RC.IsTypeAssertionContext }, RuleAction.InsertSpace),
            Rule("NoSpaceAfterTypeAssertion", SyntaxKind.GreaterThanToken, anyToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceAfterTypeAssertionOption), RC.IsNonJsxSameLineTokenContext, RC.IsTypeAssertionContext }, RuleAction.DeleteSpace),

            Rule("SpaceBeforeTypeAnnotation", anyToken, new[] { SyntaxKind.QuestionToken, SyntaxKind.ColonToken }, new List<ContextPredicate> { RC.IsOptionEnabled(RC.InsertSpaceBeforeTypeAnnotationOption), RC.IsNonJsxSameLineTokenContext, RC.IsTypeAnnotationContext }, RuleAction.InsertSpace),
            Rule("NoSpaceBeforeTypeAnnotation", anyToken, new[] { SyntaxKind.QuestionToken, SyntaxKind.ColonToken }, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefined(RC.InsertSpaceBeforeTypeAnnotationOption), RC.IsNonJsxSameLineTokenContext, RC.IsTypeAnnotationContext }, RuleAction.DeleteSpace),

            Rule("NoOptionalSemicolon", SyntaxKind.SemicolonToken, anyTokenIncludingEof, new List<ContextPredicate> { RC.OptionEquals<SemicolonPreference>(RC.SemicolonOption, SemicolonPreference.Remove), RC.IsSemicolonDeletionContext }, RuleAction.DeleteToken),
            Rule("OptionalSemicolon", anyToken, anyTokenIncludingEof, new List<ContextPredicate> { RC.OptionEquals<SemicolonPreference>(RC.SemicolonOption, SemicolonPreference.Insert), RC.IsSemicolonInsertionContext }, RuleAction.InsertTrailingSemicolon),
        };

        // These rules are lower in priority than user-configurable.
        var lowPriorityCommonRules = new List<RuleSpec>
        {
            // Space after keyword but not before ; or : or ?
            Rule("NoSpaceBeforeSemicolon", anyToken, SyntaxKind.SemicolonToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),

            Rule("SpaceBeforeOpenBraceInControl", controlOpenBraceLeftTokenRange, SyntaxKind.OpenBraceToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefinedOrTokensOnSameLine(RC.PlaceOpenBraceOnNewLineForControlBlocksOption), RC.IsControlDeclContext, RC.IsNotFormatOnEnter, RC.IsSameLineTokenOrBeforeBlockContext }, RuleAction.InsertSpace, RuleFlags.CanDeleteNewLines),
            Rule("SpaceBeforeOpenBraceInFunction", functionOpenBraceLeftTokenRange, SyntaxKind.OpenBraceToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefinedOrTokensOnSameLine(RC.PlaceOpenBraceOnNewLineForFunctionsOption), RC.IsFunctionDeclContext, RC.IsBeforeBlockContext, RC.IsNotFormatOnEnter, RC.IsSameLineTokenOrBeforeBlockContext }, RuleAction.InsertSpace, RuleFlags.CanDeleteNewLines),
            Rule("SpaceBeforeOpenBraceInTypeScriptDeclWithBlock", typeScriptOpenBraceLeftTokenRange, SyntaxKind.OpenBraceToken, new List<ContextPredicate> { RC.IsOptionDisabledOrUndefinedOrTokensOnSameLine(RC.PlaceOpenBraceOnNewLineForFunctionsOption), RC.IsTypeScriptDeclWithBlockContext, RC.IsNotFormatOnEnter, RC.IsSameLineTokenOrBeforeBlockContext }, RuleAction.InsertSpace, RuleFlags.CanDeleteNewLines),

            Rule("NoSpaceBeforeComma", anyToken, SyntaxKind.CommaToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),

            // No space before and after indexer `x[]`
            Rule("NoSpaceBeforeOpenBracket", AnyTokenExcept(SyntaxKind.AsyncKeyword, SyntaxKind.CaseKeyword), SyntaxKind.OpenBracketToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),
            Rule("NoSpaceAfterCloseBracket", SyntaxKind.CloseBracketToken, anyToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsNotBeforeBlockInFunctionDeclarationContext }, RuleAction.DeleteSpace),
            Rule("SpaceAfterSemicolon", SyntaxKind.SemicolonToken, anyToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),

            // Remove extra space between for and await
            Rule("SpaceBetweenForAndAwaitKeyword", SyntaxKind.ForKeyword, SyntaxKind.AwaitKeyword, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),

            // Remove extra spaces between ... and type name in tuple spread
            Rule("SpaceBetweenDotDotDotAndTypeName", SyntaxKind.DotDotDotToken, typeNames, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.DeleteSpace),

            // Add a space between statements.
            Rule(
                "SpaceBetweenStatements",
                new[] { SyntaxKind.CloseParenToken, SyntaxKind.DoKeyword, SyntaxKind.ElseKeyword, SyntaxKind.CaseKeyword },
                anyToken,
                new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext, RC.IsNonJsxElementOrFragmentContext, RC.IsNotForContext },
                RuleAction.InsertSpace),
            // This low-pri rule takes care of "try {", "catch {" and "finally {"
            Rule("SpaceAfterTryCatchFinally", new[] { SyntaxKind.TryKeyword, SyntaxKind.CatchKeyword, SyntaxKind.FinallyKeyword }, SyntaxKind.OpenBraceToken, new List<ContextPredicate> { RC.IsNonJsxSameLineTokenContext }, RuleAction.InsertSpace),
        };

        // Go: result = append(highPriority, userConfigurable, lowPriority)
        var result = new List<RuleSpec>(highPriorityCommonRules.Count + userConfigurableRules.Count + lowPriorityCommonRules.Count);
        result.AddRange(highPriorityCommonRules);
        result.AddRange(userConfigurableRules);
        result.AddRange(lowPriorityCommonRules);
        return result;
    }

    // Go: func tokenRangeFrom(tokens ...ast.Kind) tokenRange
    internal static TokenRange TokenRangeFrom(params SyntaxKind[] tokens)
        => new() { IsSpecific = true, Tokens = tokens };

    // Go: func tokenRangeFromEx(prefix []ast.Kind, tokens ...ast.Kind) tokenRange
    internal static TokenRange TokenRangeFromEx(SyntaxKind[] prefix, params SyntaxKind[] tokens)
    {
        // Go: tokens = append(prefix, tokens...)
        var combined = new List<SyntaxKind>(prefix.Length + tokens.Length);
        combined.AddRange(prefix);
        combined.AddRange(tokens);
        return new TokenRange { IsSpecific = true, Tokens = combined.ToArray() };
    }

    // Go: func tokenRangeFromRange(start ast.Kind, end ast.Kind) tokenRange
    internal static TokenRange TokenRangeFromRange(SyntaxKind start, SyntaxKind end)
    {
        var tokens = new List<SyntaxKind>();
        for (int token = (int)start; token <= (int)end; token++)
        {
            tokens.Add((SyntaxKind)token);
        }
        return TokenRangeFrom(tokens.ToArray());
    }
}
