// AST decorator/modifier/token predicates ported from internal/ast/utilities.go.
//
// These free functions live in Go's ast package and are called by the checker's decorator/grammar/
// variable-type paths (checkDecorators, checkGrammarModifiers, getTypeForVariableLikeDeclaration).
// They had been temporarily stubbed as Checker-local wrappers (Checker.Grammar.Deps.cs /
// Checker.MembersSignatures.Deps.cs) or were simply absent; this file lands the canonical AstUtilities
// members and those stubs are deleted. Added as a new partial of AstUtilities so no parallel-owned
// Ast/ file is touched. Grep-confirmed absent from Ts.Ast before adding.
#nullable enable

using Ts.Core;

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

    // ast/utilities.go:4269
    public static bool ClassOrConstructorParameterIsDecorated(bool useLegacyDecorators, Node node)
    {
        if (NodeIsDecorated(useLegacyDecorators, node, null, null))
        {
            return true;
        }
        var constructor = GetFirstConstructorWithBody(node);
        return constructor != null && ChildIsDecorated(useLegacyDecorators, constructor, node);
    }

    // ast/utilities.go:4312
    public static bool NodeIsDecorated(bool useLegacyDecorators, Node node, Node? parent, Node? grandparent)
    {
        return HasDecorators(node) && NodeCanBeDecorated(useLegacyDecorators, node, parent, grandparent);
    }

    // ast/utilities.go:4316
    public static bool NodeOrChildIsDecorated(bool useLegacyDecorators, Node node, Node? parent, Node? grandparent)
    {
        return NodeIsDecorated(useLegacyDecorators, node, parent, grandparent) || ChildIsDecorated(useLegacyDecorators, node, parent);
    }

    // ast/utilities.go:4320
    public static bool ChildIsDecorated(bool useLegacyDecorators, Node node, Node? parent)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
                return CoreUtil.Some(node.Members(), m => NodeOrChildIsDecorated(useLegacyDecorators, m, node, parent));
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.Constructor:
                return CoreUtil.Some(node.Parameters(), p => NodeIsDecorated(useLegacyDecorators, p, node, parent));
            default:
                return false;
        }
    }

    // ast/utilities.go:4277-4310
    public static bool ClassElementOrClassElementParameterIsDecorated(bool useLegacyDecorators, Node node, Node parent)
    {
        NodeList? parameters = null;
        if (IsAccessor(node))
        {
            var decls = GetAllAccessorDeclarations(parent.Members() ?? [], node);
            Node? firstAccessorWithDecorators = null;
            if (HasDecorators(decls.FirstAccessor!))
            {
                firstAccessorWithDecorators = decls.FirstAccessor;
            }
            else if (decls.SecondAccessor != null && HasDecorators(decls.SecondAccessor))
            {
                firstAccessorWithDecorators = decls.SecondAccessor;
            }
            if (firstAccessorWithDecorators == null || node != firstAccessorWithDecorators)
            {
                return false;
            }
            if (decls.SetAccessor != null)
            {
                parameters = decls.SetAccessor.Parameters;
            }
        }
        else if (NodeTests.IsMethodDeclaration(node))
        {
            parameters = node.ParameterList();
        }
        if (NodeIsDecorated(useLegacyDecorators, node, parent, null))
        {
            return true;
        }
        if (parameters != null && parameters.Nodes.Length > 0)
        {
            foreach (var parameter in parameters.Nodes)
            {
                if (IsThisParameter(parameter))
                {
                    continue;
                }
                if (NodeIsDecorated(useLegacyDecorators, parameter, node, parent))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // ast/utilities.go:4336-4342
    public sealed class AllAccessorDeclarations
    {
        public Node? FirstAccessor;
        public Node? SecondAccessor;
        public SetAccessorDeclaration? SetAccessor;
        public GetAccessorDeclaration? GetAccessor;
    }

    // ast/utilities.go:4344-4392
    public static AllAccessorDeclarations GetAllAccessorDeclarationsForDeclaration(Node accessor, Node[] declarationsOfSymbol)
    {
        SyntaxKind otherKind;
        if (accessor.Kind == SyntaxKind.SetAccessor)
        {
            otherKind = SyntaxKind.GetAccessor;
        }
        else if (accessor.Kind == SyntaxKind.GetAccessor)
        {
            otherKind = SyntaxKind.SetAccessor;
        }
        else
        {
            throw new InvalidOperationException($"Unexpected node kind {accessor.Kind}");
        }
        // otherAccessor := GetDeclarationOfKind(c.getSymbolOfDeclaration(accessor), otherKind)
        Node? otherAccessor = null;
        foreach (var d in declarationsOfSymbol)
        {
            if (d.Kind == otherKind)
            {
                otherAccessor = d;
                break;
            }
        }

        Node? firstAccessor;
        Node? secondAccessor;
        if (otherAccessor != null && otherAccessor.Loc.Pos < accessor.Loc.Pos)
        {
            firstAccessor = otherAccessor;
            secondAccessor = accessor;
        }
        else
        {
            firstAccessor = accessor;
            secondAccessor = otherAccessor;
        }

        SetAccessorDeclaration? setAccessor = null;
        GetAccessorDeclaration? getAccessor = null;
        if (accessor.Kind == SyntaxKind.SetAccessor)
        {
            setAccessor = accessor.AsSetAccessorDeclaration();
            if (otherAccessor != null)
            {
                getAccessor = otherAccessor.AsGetAccessorDeclaration();
            }
        }
        else
        {
            getAccessor = accessor.AsGetAccessorDeclaration();
            if (otherAccessor != null)
            {
                setAccessor = otherAccessor.AsSetAccessorDeclaration();
            }
        }

        return new AllAccessorDeclarations
        {
            FirstAccessor = firstAccessor,
            SecondAccessor = secondAccessor,
            SetAccessor = setAccessor,
            GetAccessor = getAccessor,
        };
    }

    // ast/utilities.go:4394-4414
    public static AllAccessorDeclarations GetAllAccessorDeclarations(Node[] parentDeclarations, Node accessor)
    {
        if (HasDynamicName(accessor))
        {
            // dynamic names can only be match up via checker symbol lookup, just return an object with just this accessor
            return GetAllAccessorDeclarationsForDeclaration(accessor, [accessor]);
        }

        var accessorName = GetPropertyNameForPropertyNameNode(accessor.Name()!);
        var accessorStatic = IsStatic(accessor);
        var matches = new List<Node>();
        foreach (var member in parentDeclarations)
        {
            if (!IsAccessor(member) || IsStatic(member) != accessorStatic)
            {
                continue;
            }
            var memberName = GetPropertyNameForPropertyNameNode(member.Name()!);
            if (memberName == accessorName)
            {
                matches.Add(member);
            }
        }
        return GetAllAccessorDeclarationsForDeclaration(accessor, matches.ToArray());
    }

    // ast/utilities.go:3141-3161
    public static string GetPropertyNameForPropertyNameNode(Node name)
    {
        switch (name.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.JsxNamespacedName:
                return name.Text();
            case SyntaxKind.ComputedPropertyName:
                var nameExpression = name.Expression()!;
                if (IsStringOrNumericLiteralLike(nameExpression))
                {
                    return nameExpression.Text();
                }
                if (IsSignedNumericLiteral(nameExpression))
                {
                    var text = nameExpression.AsPrefixUnaryExpression().Operand!.Text();
                    if (nameExpression.AsPrefixUnaryExpression().Operator == SyntaxKind.MinusToken)
                    {
                        text = "-" + text;
                    }
                    return text;
                }
                return InternalSymbolName.Missing;
        }
        throw new InvalidOperationException("Unhandled case in getPropertyNameForPropertyNameNode");
    }
}
