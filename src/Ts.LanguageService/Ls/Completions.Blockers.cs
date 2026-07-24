// Ported from tsgo internal/ls/completions.go — the completion-list blocker cluster
// (isCompletionListBlocker and every predicate it reaches), plus the small keyword/editing
// helpers they share.

using System.Text;
using Ts.Ast;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // completions.go:4594-4607 — isCompletionListBlocker.
    internal static bool IsCompletionListBlocker(
        Node contextToken,
        Node? previousToken,
        Node location,
        SourceFile file,
        int position,
        global::Ts.Checker.Checker typeChecker)
    {
        return IsInStringOrRegularExpressionOrTemplateLiteral(contextToken, position) ||
            IsSolelyIdentifierDefinitionLocation(contextToken, previousToken, file, position, typeChecker) ||
            IsDotOfNumericLiteral(contextToken, file) ||
            IsInJsxText(contextToken, location) ||
            NodeTests.IsBigIntLiteral(contextToken);
    }

    // completions.go:4609-4618 — isInStringOrRegularExpressionOrTemplateLiteral.
    internal static bool IsInStringOrRegularExpressionOrTemplateLiteral(Node contextToken, int position)
    {
        // To be "in" one of these literals, the position has to be:
        //   1. entirely within the token text.
        //   2. at the end position of an unterminated token.
        //   3. at the end of a regular expression (due to trailing flags like '/foo/g').
        return ((NodeTests.IsRegularExpressionLiteral(contextToken) || AstUtilities.IsStringTextContainingNode(contextToken)) &&
            contextToken.Loc.ContainsExclusive(position)) ||
            (position == contextToken.End &&
                (AstUtilities.IsUnterminatedLiteral(contextToken) || NodeTests.IsRegularExpressionLiteral(contextToken)));
    }

    // completions.go:4620-4765 — isSolelyIdentifierDefinitionLocation: true if we are certain that
    // the currently edited location must define a new location; false otherwise.
    internal static bool IsSolelyIdentifierDefinitionLocation(
        Node contextToken,
        Node? previousToken,
        SourceFile file,
        int position,
        global::Ts.Checker.Checker typeChecker)
    {
        var parent = contextToken.Parent!;
        var containingNodeKind = parent.Kind;
        switch (contextToken.Kind)
        {
            case SyntaxKind.CommaToken:
                return containingNodeKind == SyntaxKind.VariableDeclaration ||
                    IsVariableDeclarationListButNotTypeArgument(contextToken, file, typeChecker) ||
                    containingNodeKind == SyntaxKind.VariableStatement ||
                    containingNodeKind == SyntaxKind.EnumDeclaration || // enum a { foo, |
                    IsFunctionLikeButNotConstructor(containingNodeKind) ||
                    containingNodeKind == SyntaxKind.InterfaceDeclaration || // interface A<T, |
                    containingNodeKind == SyntaxKind.ArrayBindingPattern || // var [x, y|
                    containingNodeKind == SyntaxKind.TypeAliasDeclaration || // type Map, K, |
                    // class A<T, |
                    // var C = class D<T, |
                    (AstUtilities.IsClassLike(parent) && parent.TypeParameterList() != null && parent.TypeParameterList()!.End >= contextToken.Pos);
            case SyntaxKind.DotToken:
                return containingNodeKind == SyntaxKind.ArrayBindingPattern; // var [.|
            case SyntaxKind.ColonToken:
                return containingNodeKind == SyntaxKind.BindingElement; // var {x :html|
            case SyntaxKind.OpenBracketToken:
                return containingNodeKind == SyntaxKind.ArrayBindingPattern; // var [x|
            case SyntaxKind.OpenParenToken:
                return containingNodeKind == SyntaxKind.CatchClause || IsFunctionLikeButNotConstructor(containingNodeKind);
            case SyntaxKind.OpenBraceToken:
                return containingNodeKind == SyntaxKind.EnumDeclaration; // enum a { |
            case SyntaxKind.LessThanToken:
                return containingNodeKind == SyntaxKind.ClassDeclaration || // class A< |
                    containingNodeKind == SyntaxKind.ClassExpression || // var C = class D< |
                    containingNodeKind == SyntaxKind.InterfaceDeclaration || // interface A< |
                    containingNodeKind == SyntaxKind.TypeAliasDeclaration || // type List< |
                    AstUtilities.IsFunctionLikeKind(containingNodeKind);
            case SyntaxKind.StaticKeyword:
                return containingNodeKind == SyntaxKind.PropertyDeclaration &&
                    !AstUtilities.IsClassLike(parent.Parent!);
            case SyntaxKind.DotDotDotToken:
                return containingNodeKind == SyntaxKind.Parameter ||
                    (parent.Parent != null && parent.Parent.Kind == SyntaxKind.ArrayBindingPattern); // var [...z|
            case SyntaxKind.PublicKeyword:
            case SyntaxKind.PrivateKeyword:
            case SyntaxKind.ProtectedKeyword:
                return containingNodeKind == SyntaxKind.Parameter && !NodeTests.IsConstructorDeclaration(parent.Parent!);
            case SyntaxKind.AsKeyword:
                return containingNodeKind == SyntaxKind.ImportSpecifier ||
                    containingNodeKind == SyntaxKind.ExportSpecifier ||
                    containingNodeKind == SyntaxKind.NamespaceImport;
            case SyntaxKind.GetKeyword:
            case SyntaxKind.SetKeyword:
                return !IsFromObjectTypeDeclaration(contextToken);
            case SyntaxKind.Identifier:
            {
                if ((containingNodeKind == SyntaxKind.ImportSpecifier || containingNodeKind == SyntaxKind.ExportSpecifier) &&
                    contextToken == parent.Name() &&
                    contextToken.Text() == "type")
                {
                    // import { type | }
                    return false;
                }
                var ancestorVariableDeclaration = AstUtilities.FindAncestor(parent, NodeTests.IsVariableDeclaration);
                if (ancestorVariableDeclaration != null && LsDeps.GetLineEndOfPosition(file, contextToken.End) < position)
                {
                    // let a
                    // |
                    return false;
                }
                break;
            }
            case SyntaxKind.ClassKeyword:
            case SyntaxKind.EnumKeyword:
            case SyntaxKind.InterfaceKeyword:
            case SyntaxKind.FunctionKeyword:
            case SyntaxKind.VarKeyword:
            case SyntaxKind.ImportKeyword:
            case SyntaxKind.LetKeyword:
            case SyntaxKind.ConstKeyword:
            case SyntaxKind.InferKeyword:
                return true;
            case SyntaxKind.TypeKeyword:
                // import { type foo| }
                return containingNodeKind != SyntaxKind.ImportSpecifier;
            case SyntaxKind.AsteriskToken:
                return AstUtilities.IsFunctionLike(parent) && !NodeTests.IsMethodDeclaration(parent);
        }

        var tokenKind = KeywordForNode(contextToken);
        // If the previous token is keyword corresponding to class member completion keyword
        // there will be completion available here
        if (IsClassMemberCompletionKeyword(tokenKind) && IsFromObjectTypeDeclaration(contextToken))
        {
            return false;
        }

        if (IsConstructorParameterCompletion(contextToken))
        {
            // constructor parameter completion is available only if
            // - its modifier of the constructor parameter or
            // - its name of the parameter and not being edited
            // eg. constructor(a |<- this shouldnt show completion
            if (!NodeTests.IsIdentifier(contextToken) ||
                AstUtilities.IsParameterPropertyModifier(tokenKind) ||
                IsCurrentlyEditingNode(contextToken, file, position))
            {
                return false;
            }
        }

        // Previous token may have been a keyword that was converted to an identifier.
        switch (KeywordForNode(contextToken))
        {
            case SyntaxKind.AbstractKeyword:
            case SyntaxKind.ClassKeyword:
            case SyntaxKind.DeclareKeyword:
            case SyntaxKind.EnumKeyword:
            case SyntaxKind.FunctionKeyword:
            case SyntaxKind.InterfaceKeyword:
            case SyntaxKind.LetKeyword:
            case SyntaxKind.PrivateKeyword:
            case SyntaxKind.ProtectedKeyword:
            case SyntaxKind.PublicKeyword:
            case SyntaxKind.StaticKeyword:
            case SyntaxKind.VarKeyword:
                return true;
            case SyntaxKind.AsyncKeyword:
                return NodeTests.IsPropertyDeclaration(contextToken.Parent!);
        }

        // If we are inside a class declaration, and `constructor` is totally not present,
        // but we request a completion manually at a whitespace...
        var ancestorClassLike = AstUtilities.FindAncestor(parent, AstUtilities.IsClassLike);
        if (ancestorClassLike != null && contextToken == previousToken &&
            IsPreviousPropertyDeclarationTerminated(contextToken, file, position))
        {
            // Don't block completions.
            return false;
        }

        var ancestorPropertyDeclaration = AstUtilities.FindAncestor(parent, NodeTests.IsPropertyDeclaration);
        // If we are inside a class declaration and typing `constructor` after property declaration...
        if (ancestorPropertyDeclaration != null && contextToken != previousToken &&
            previousToken?.Parent?.Parent != null && AstUtilities.IsClassLike(previousToken.Parent.Parent) &&
            // And the cursor is at the token...
            position <= previousToken.End)
        {
            // If we are sure that the previous property declaration is terminated according to newline or semicolon...
            if (IsPreviousPropertyDeclarationTerminated(contextToken, file, previousToken.End))
            {
                // Don't block completions.
                return false;
            }
            else if (contextToken.Kind != SyntaxKind.EqualsToken &&
                // Should not block: `class C { blah = c/**/ }`
                // But should block: `class C { blah = somewhat c/**/ }` and `class C { blah: SomeType c/**/ }`
                (AstUtilities.IsInitializedProperty(ancestorPropertyDeclaration) || ancestorPropertyDeclaration.Type() != null))
            {
                return true;
            }
        }
        if (tokenKind == SyntaxKind.ConstKeyword)
        {
            return true;
        }
        return AstUtilities.IsDeclarationName(contextToken) &&
            !NodeTests.IsShorthandPropertyAssignment(parent) &&
            !NodeTests.IsJsxAttribute(parent) &&
            // Don't block completions if we're in `class C /**/`, `interface I /**/` or `<T /**/>` ,
            // because we're *past* the end of the identifier and might want to complete `extends`.
            // If `contextToken !== previousToken`, this is `class C ex/**/`, `interface I ex/**/` or `<T ex/**/>`.
            !((AstUtilities.IsClassLike(parent) || NodeTests.IsInterfaceDeclaration(parent) || NodeTests.IsTypeParameterDeclaration(parent)) &&
                (contextToken != previousToken || position > previousToken!.End));
    }

    // completions.go:4767-4770 — isVariableDeclarationListButNotTypeArgument.
    internal static bool IsVariableDeclarationListButNotTypeArgument(Node node, SourceFile file, global::Ts.Checker.Checker typeChecker)
    {
        return node.Parent!.Kind == SyntaxKind.VariableDeclarationList &&
            !IsPossiblyTypeArgumentPosition(node, file, typeChecker);
    }

    // completions.go:2765-2770 — isPossiblyTypeArgumentPosition.
    internal static bool IsPossiblyTypeArgumentPosition(Node token, SourceFile sourceFile, global::Ts.Checker.Checker typeChecker)
    {
        var info = LsTypeArgs.GetPossibleTypeArgumentsInfo(token, sourceFile);
        return info != null && (AstUtilities.IsPartOfTypeNode(info.called) ||
            LsTypeArgs.GetPossibleGenericSignatures(info.called, info.nTypeArguments, typeChecker).Length != 0 ||
            IsPossiblyTypeArgumentPosition(info.called, sourceFile, typeChecker));
    }

    // completions.go:4772-4774 — isFunctionLikeButNotConstructor.
    internal static bool IsFunctionLikeButNotConstructor(SyntaxKind kind) =>
        AstUtilities.IsFunctionLikeKind(kind) && kind != SyntaxKind.Constructor;

    // completions.go:4776-4780 — isPreviousPropertyDeclarationTerminated.
    internal static bool IsPreviousPropertyDeclarationTerminated(Node contextToken, SourceFile file, int position)
    {
        return contextToken.Kind != SyntaxKind.EqualsToken &&
            (contextToken.Kind == SyntaxKind.SemicolonToken ||
                LsDeps.GetLineOfPosition(file, contextToken.End) != LsDeps.GetLineOfPosition(file, position));
    }

    // completions.go:4782-4789 — isDotOfNumericLiteral.
    internal static bool IsDotOfNumericLiteral(Node contextToken, SourceFile file)
    {
        if (contextToken.Kind == SyntaxKind.NumericLiteral)
        {
            var text = file.Text()[contextToken.Pos..contextToken.End];
            return text.Length > 0 && text[^1] == '.';
        }
        return false;
    }

    // completions.go:4791-4821 — isInJsxText.
    internal static bool IsInJsxText(Node contextToken, Node location)
    {
        if (contextToken.Kind == SyntaxKind.JsxText)
        {
            return true;
        }

        if (contextToken.Kind == SyntaxKind.GreaterThanToken && contextToken.Parent != null)
        {
            // <Component<string> /**/ />
            // <Component<string> /**/ ><Component>
            // - contextToken: GreaterThanToken (before cursor)
            // - location: JsxSelfClosingElement or JsxOpeningElement
            // - contextToken.parent === location
            if (location == contextToken.Parent && AstUtilities.IsJsxOpeningLikeElement(location))
            {
                return false;
            }

            if (contextToken.Parent.Kind == SyntaxKind.JsxOpeningElement)
            {
                // <div>/**/
                // - contextToken: GreaterThanToken (before cursor)
                // - location: JSXElement
                // - different parents (JSXOpeningElement, JSXElement)
                return location.Parent!.Kind != SyntaxKind.JsxOpeningElement;
            }

            if (contextToken.Parent.Kind == SyntaxKind.JsxClosingElement ||
                contextToken.Parent.Kind == SyntaxKind.JsxSelfClosingElement)
            {
                return contextToken.Parent.Parent != null && contextToken.Parent.Parent.Kind == SyntaxKind.JsxElement;
            }
        }

        return false;
    }

    // completions.go:3633-3638 — keywordForNode.
    internal static SyntaxKind KeywordForNode(Node node)
    {
        if (NodeTests.IsIdentifier(node))
        {
            return global::Ts.Scanner.Scanner.IdentifierToKeywordKind(node.AsIdentifier());
        }
        return node.Kind;
    }

    // completions.go:3956-3959 — isCurrentlyEditingNode.
    internal static bool IsCurrentlyEditingNode(Node node, SourceFile file, int position)
    {
        var start = global::Ts.AstNav.Tokens.GetStartOfNode(node, file, false /*includeJSDoc*/);
        return start <= position && position <= node.End;
    }

    // completions.go:3999-4002 — isConstructorParameterCompletion.
    internal static bool IsConstructorParameterCompletion(Node node)
    {
        return node.Parent != null && NodeTests.IsParameterDeclaration(node.Parent) && node.Parent.Parent != null && NodeTests.IsConstructorDeclaration(node.Parent.Parent) &&
            (AstUtilities.IsParameterPropertyModifier(node.Kind) || AstUtilities.IsDeclarationName(node));
    }

    // completions.go:4103-4105 — isFromObjectTypeDeclaration.
    internal static bool IsFromObjectTypeDeclaration(Node node)
    {
        return node.Parent != null && AstUtilities.IsClassOrTypeElement(node.Parent) && node.Parent.Parent != null && AstUtilities.IsObjectTypeDeclaration(node.Parent.Parent);
    }

    // completions.go:3505-3631 — computeCommitCharactersAndIsNewIdentifier.
    internal static (bool IsNewIdentifierLocation, string[] DefaultCommitCharacters) ComputeCommitCharactersAndIsNewIdentifier(
        Node? contextToken,
        SourceFile file,
        int position)
    {
        if (contextToken == null)
        {
            return (false, AllCommitCharacters);
        }
        var containingNodeKind = contextToken.Parent!.Kind;
        var tokenKind = KeywordForNode(contextToken);
        // Previous token may have been a keyword that was converted to an identifier.
        switch (tokenKind)
        {
            case SyntaxKind.CommaToken:
                switch (containingNodeKind)
                {
                    // func( a, |
                    // new C(a, |
                    case SyntaxKind.CallExpression:
                    case SyntaxKind.NewExpression:
                    {
                        var expression = contextToken.Parent.Expression()!;
                        // func\n(a, |
                        if (LsDeps.GetLineOfPosition(file, expression.End) != LsDeps.GetLineOfPosition(file, position))
                        {
                            return (true, NoCommaCommitCharacters);
                        }
                        return (true, AllCommitCharacters);
                    }
                    // const x = (a, |
                    case SyntaxKind.BinaryExpression:
                        return (true, NoCommaCommitCharacters);
                    // constructor( a, | /* public, protected, private keywords are allowed here, so show completion */
                    // var x: (s: string, list|
                    // const obj = { x, |
                    case SyntaxKind.Constructor:
                    case SyntaxKind.FunctionType:
                    case SyntaxKind.ObjectLiteralExpression:
                        return (true, EmptyCommitCharacters);
                    // [a, |
                    case SyntaxKind.ArrayLiteralExpression:
                        return (true, AllCommitCharacters);
                    default:
                        return (false, AllCommitCharacters);
                }
            case SyntaxKind.OpenParenToken:
                switch (containingNodeKind)
                {
                    // func( |
                    // new C(a|
                    case SyntaxKind.CallExpression:
                    case SyntaxKind.NewExpression:
                    {
                        var expression = contextToken.Parent.Expression()!;
                        // func\n( |
                        if (LsDeps.GetLineOfPosition(file, expression.End) != LsDeps.GetLineOfPosition(file, position))
                        {
                            return (true, NoCommaCommitCharacters);
                        }
                        return (true, AllCommitCharacters);
                    }
                    // const x = (a|
                    case SyntaxKind.ParenthesizedExpression:
                        return (true, NoCommaCommitCharacters);
                    // constructor( |
                    // function F(pred: (a| /* this can become an arrow function, where 'a' is the argument */
                    case SyntaxKind.Constructor:
                    case SyntaxKind.ParenthesizedType:
                        return (true, EmptyCommitCharacters);
                    default:
                        return (false, AllCommitCharacters);
                }
            case SyntaxKind.OpenBracketToken:
                switch (containingNodeKind)
                {
                    // [ |
                    // [ | : string ]
                    // [ |    /* this can become an index signature */
                    case SyntaxKind.ArrayLiteralExpression:
                    case SyntaxKind.IndexSignature:
                    case SyntaxKind.TupleType:
                    case SyntaxKind.ComputedPropertyName:
                        return (true, AllCommitCharacters);
                    default:
                        return (false, AllCommitCharacters);
                }
            // module |
            // namespace |
            // import |
            case SyntaxKind.ModuleKeyword:
            case SyntaxKind.NamespaceKeyword:
            case SyntaxKind.ImportKeyword:
                return (true, EmptyCommitCharacters);
            case SyntaxKind.DotToken:
                switch (containingNodeKind)
                {
                    // module A.|
                    case SyntaxKind.ModuleDeclaration:
                        return (true, EmptyCommitCharacters);
                    default:
                        return (false, AllCommitCharacters);
                }
            case SyntaxKind.OpenBraceToken:
                switch (containingNodeKind)
                {
                    // class A { |
                    // const obj = { |
                    case SyntaxKind.ClassDeclaration:
                    case SyntaxKind.ObjectLiteralExpression:
                        return (true, EmptyCommitCharacters);
                    default:
                        return (false, AllCommitCharacters);
                }
            case SyntaxKind.EqualsToken:
                switch (containingNodeKind)
                {
                    // const x = a|
                    // x = a|
                    case SyntaxKind.VariableDeclaration:
                    case SyntaxKind.BinaryExpression:
                        return (true, AllCommitCharacters);
                    default:
                        return (false, AllCommitCharacters);
                }
            case SyntaxKind.TemplateHead:
                // `aa ${|
                return (containingNodeKind == SyntaxKind.TemplateExpression, AllCommitCharacters);
            case SyntaxKind.TemplateMiddle:
                // `aa ${10} dd ${|
                return (containingNodeKind == SyntaxKind.TemplateSpan, AllCommitCharacters);
            case SyntaxKind.AsyncKeyword:
                // const obj = { async c|()
                // const obj = { async c|
                if (containingNodeKind == SyntaxKind.MethodDeclaration || containingNodeKind == SyntaxKind.ShorthandPropertyAssignment)
                {
                    return (true, EmptyCommitCharacters);
                }
                return (false, AllCommitCharacters);
            case SyntaxKind.AsteriskToken:
                // const obj = { * c|
                if (containingNodeKind == SyntaxKind.MethodDeclaration)
                {
                    return (true, EmptyCommitCharacters);
                }
                return (false, AllCommitCharacters);
        }

        if (IsClassMemberCompletionKeyword(tokenKind))
        {
            return (true, EmptyCommitCharacters);
        }

        return (false, AllCommitCharacters);
    }

    // completions.go:4244-4247 — isTypeKeywordTokenOrIdentifier.
    internal static bool IsTypeKeywordTokenOrIdentifier(Node node)
    {
        return AstUtilities.IsTypeKeywordToken(node) ||
            (NodeTests.IsIdentifier(node) && global::Ts.Scanner.Scanner.IdentifierToKeywordKind(node.AsIdentifier()) == SyntaxKind.TypeKeyword);
    }
}
