// AST decorator/modifier/token predicates ported from internal/ast/utilities.go.
//
// These free functions live in Go's ast package and are called by the checker's decorator/grammar/
// variable-type paths (checkDecorators, checkGrammarModifiers, getTypeForVariableLikeDeclaration).
// They had been temporarily stubbed as Checker-local wrappers (Checker.Grammar.Deps.cs /
// Checker.MembersSignatures.Deps.cs) or were simply absent; this file lands the canonical AstUtilities
// members and those stubs are deleted. Added as a new partial of AstUtilities so no parallel-owned
// Ast/ file is touched. Grep-confirmed absent from Ts.Ast before adding.
#nullable enable

namespace Ts.Ast;

public static partial class AstUtilities
{
    // utilities.go:3749-3751
    public static bool HasDecorators(Node node)
    {
        return HasSyntacticModifier(node, ModifierFlags.Decorator);
    }

    // utilities.go:3695-3697
    public static bool HasQuestionToken(Node node)
    {
        return ParserStatementsAstGaps.IsQuestionToken(node.QuestionToken());
    }

    // utilities.go:4227-4229
    public static bool HasAbstractModifier(Node node)
    {
        return HasSyntacticModifier(node, ModifierFlags.Abstract);
    }

    // utilities.go:4231-4233
    public static bool HasAmbientModifier(Node node)
    {
        return HasSyntacticModifier(node, ModifierFlags.Ambient);
    }

    // utilities.go:1348-1350
    public static bool IsThisParameter(Node node)
    {
        return NodeTests.IsParameterDeclaration(node) && node.Name() != null && JSDocReparseAstGaps.IsThisIdentifier(node.Name());
    }

    // utilities.go:3281-3290
    public static Node? GetThisParameter(Node signature)
    {
        // callback tags do not currently support this parameters
        if (signature.Parameters().Length != 0)
        {
            var thisParameter = signature.Parameters()[0];
            if (IsThisParameter(thisParameter))
            {
                return thisParameter;
            }
        }
        return null;
    }

    // utilities.go:4235-4267
    public static bool NodeCanBeDecorated(bool useLegacyDecorators, Node node, Node? parent, Node? grandparent)
    {
        // private names cannot be used with decorators yet
        if (useLegacyDecorators && node.Name() != null && NodeTests.IsPrivateIdentifier(node.Name()!))
        {
            return false;
        }
        switch (node.Kind)
        {
            case SyntaxKind.ClassDeclaration:
                // class declarations are valid targets
                return true;
            case SyntaxKind.ClassExpression:
                // class expressions are valid targets for native decorators
                return !useLegacyDecorators;
            case SyntaxKind.PropertyDeclaration:
                // property declarations are valid if their parent is a class declaration.
                return parent != null && (useLegacyDecorators && NodeTests.IsClassDeclaration(parent) ||
                    !useLegacyDecorators && IsClassLike(parent) && !HasAbstractModifier(node) && !HasAmbientModifier(node));
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.MethodDeclaration:
                // if this method has a body and its parent is a class declaration, this is a valid target.
                return parent != null && node.Body() != null && (useLegacyDecorators && NodeTests.IsClassDeclaration(parent) ||
                    !useLegacyDecorators && IsClassLike(parent));
            case SyntaxKind.Parameter:
                // TODO(rbuckton): ParameterDeclaration decorator support for ES decorators must wait until it is standardized
                if (!useLegacyDecorators)
                {
                    return false;
                }
                // if the parameter's parent has a body and its grandparent is a class declaration, this is a valid target.
                return parent != null && parent.Body() != null &&
                    (parent.Kind == SyntaxKind.Constructor || parent.Kind == SyntaxKind.MethodDeclaration || parent.Kind == SyntaxKind.SetAccessor) &&
                    GetThisParameter(parent) != node && grandparent != null && grandparent.Kind == SyntaxKind.ClassDeclaration;
        }

        return false;
    }
}
