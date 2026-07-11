using System;

namespace Ts.Ast;

// Go: internal/ast/precedence.go — `type OperatorPrecedence int`. Members drop the Go
// `OperatorPrecedence` prefix; grammar-term casing (LogicalOR/BitwiseXOR/…) kept for
// grep parity. iota order preserved.
public enum OperatorPrecedence : int
{
    Comma,
    Spread,
    Yield,
    Assignment,
    Conditional,
    LogicalOR,
    LogicalAND,
    BitwiseOR,
    BitwiseXOR,
    BitwiseAND,
    Equality,
    Relational,
    Shift,
    Additive,
    Multiplicative,
    Exponentiation,
    Unary,
    Update,
    LeftHandSide,
    OptionalChain,
    Member,
    Primary,
    Parentheses,
    Lowest = Comma,
    Highest = Parentheses,
    DisallowComma = Yield,
    Coalesce = LogicalOR,
    // -1 is lower than all other precedences. Returning it will cause binary expression
    // parsing to stop.
    Invalid = -1,
}

// precedence.go:214-220
[Flags]
public enum OperatorPrecedenceFlags : int
{
    None = 0,
    NewWithoutArguments = 1 << 0,
    OptionalChain = 1 << 1,
}

// precedence.go:403-652 — `type TypePrecedence int32`.
public enum TypePrecedence : int
{
    Conditional,
    JSDoc,
    Function,
    Union,
    Intersection,
    TypeOperator,
    Postfix,
    NonArray,
    Lowest = Conditional,
    Highest = NonArray,
}

public static class Precedence
{
    // precedence.go:189-200
    private static SyntaxKind GetOperator(Node expression)
    {
        switch (expression.Kind)
        {
            case SyntaxKind.BinaryExpression:
                return expression.AsBinaryExpression().OperatorToken!.Kind;
            case SyntaxKind.PrefixUnaryExpression:
                return expression.AsPrefixUnaryExpression().Operator;
            case SyntaxKind.PostfixUnaryExpression:
                return expression.AsPostfixUnaryExpression().Operator;
            default:
                return expression.Kind;
        }
    }

    // precedence.go:203-212 — Gets the precedence of an expression.
    public static OperatorPrecedence GetExpressionPrecedence(Node expression)
    {
        var op = GetOperator(expression);
        var flags = OperatorPrecedenceFlags.None;
        if (expression.Kind == SyntaxKind.NewExpression && expression.ArgumentList() == null)
        {
            flags = OperatorPrecedenceFlags.NewWithoutArguments;
        }
        else if (AstUtilities.IsOptionalChain(expression))
        {
            flags = OperatorPrecedenceFlags.OptionalChain;
        }
        return GetOperatorPrecedence(expression.Kind, op, flags);
    }

    // precedence.go:223-333 — Gets the precedence of an operator.
    public static OperatorPrecedence GetOperatorPrecedence(SyntaxKind nodeKind, SyntaxKind operatorKind, OperatorPrecedenceFlags flags)
    {
        switch (nodeKind)
        {
            case SyntaxKind.SpreadElement:
                return OperatorPrecedence.Spread;
            case SyntaxKind.YieldExpression:
                return OperatorPrecedence.Yield;
            // !!! By necessity, this differs from the old compiler to better align with ParenthesizerRules. consider backporting
            case SyntaxKind.ArrowFunction:
                return OperatorPrecedence.Assignment;
            case SyntaxKind.ConditionalExpression:
                return OperatorPrecedence.Conditional;
            case SyntaxKind.BinaryExpression:
                switch (operatorKind)
                {
                    case SyntaxKind.CommaToken:
                        return OperatorPrecedence.Comma;
                    case SyntaxKind.EqualsToken:
                    case SyntaxKind.PlusEqualsToken:
                    case SyntaxKind.MinusEqualsToken:
                    case SyntaxKind.AsteriskAsteriskEqualsToken:
                    case SyntaxKind.AsteriskEqualsToken:
                    case SyntaxKind.SlashEqualsToken:
                    case SyntaxKind.PercentEqualsToken:
                    case SyntaxKind.LessThanLessThanEqualsToken:
                    case SyntaxKind.GreaterThanGreaterThanEqualsToken:
                    case SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken:
                    case SyntaxKind.AmpersandEqualsToken:
                    case SyntaxKind.CaretEqualsToken:
                    case SyntaxKind.BarEqualsToken:
                    case SyntaxKind.BarBarEqualsToken:
                    case SyntaxKind.AmpersandAmpersandEqualsToken:
                    case SyntaxKind.QuestionQuestionEqualsToken:
                        return OperatorPrecedence.Assignment;
                    default:
                        return GetBinaryOperatorPrecedence(operatorKind);
                }
            // TODO: Should prefix `++` and `--` be moved to the `Update` precedence?
            case SyntaxKind.TypeAssertionExpression:
            case SyntaxKind.NonNullExpression:
            case SyntaxKind.PrefixUnaryExpression:
            case SyntaxKind.TypeOfExpression:
            case SyntaxKind.VoidExpression:
            case SyntaxKind.DeleteExpression:
            case SyntaxKind.AwaitExpression:
                return OperatorPrecedence.Unary;
            case SyntaxKind.PostfixUnaryExpression:
                return OperatorPrecedence.Update;
            // !!! By necessity, this differs from the old compiler to better align with ParenthesizerRules. consider backporting
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ElementAccessExpression:
                if ((flags & OperatorPrecedenceFlags.OptionalChain) != 0)
                {
                    return OperatorPrecedence.OptionalChain;
                }
                return OperatorPrecedence.Member;
            case SyntaxKind.CallExpression:
                if ((flags & OperatorPrecedenceFlags.OptionalChain) != 0)
                {
                    return OperatorPrecedence.OptionalChain;
                }
                return OperatorPrecedence.Member;
            // !!! By necessity, this differs from the old compiler to better align with ParenthesizerRules. consider backporting
            case SyntaxKind.NewExpression:
                if ((flags & OperatorPrecedenceFlags.NewWithoutArguments) != 0)
                {
                    return OperatorPrecedence.LeftHandSide;
                }
                return OperatorPrecedence.Member;
            // !!! By necessity, this differs from the old compiler to better align with ParenthesizerRules. consider backporting
            case SyntaxKind.TaggedTemplateExpression:
            case SyntaxKind.MetaProperty:
            case SyntaxKind.ExpressionWithTypeArguments:
                return OperatorPrecedence.Member;
            case SyntaxKind.AsExpression:
            case SyntaxKind.SatisfiesExpression:
                return OperatorPrecedence.Relational;
            case SyntaxKind.ThisKeyword:
            case SyntaxKind.SuperKeyword:
            case SyntaxKind.ImportKeyword:
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
            case SyntaxKind.NullKeyword:
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.ArrayLiteralExpression:
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.RegularExpressionLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.TemplateExpression:
            case SyntaxKind.OmittedExpression:
            case SyntaxKind.JsxElement:
            case SyntaxKind.JsxSelfClosingElement:
            case SyntaxKind.JsxFragment:
            case SyntaxKind.MissingDeclaration:
                return OperatorPrecedence.Primary;
            // !!! By necessity, this differs from the old compiler to support emit. consider backporting
            case SyntaxKind.ParenthesizedExpression:
                return OperatorPrecedence.Parentheses;
            default:
                return OperatorPrecedence.Invalid;
        }
    }

    // precedence.go:336-367 — Gets the precedence of a binary operator.
    public static OperatorPrecedence GetBinaryOperatorPrecedence(SyntaxKind operatorKind)
    {
        switch (operatorKind)
        {
            case SyntaxKind.QuestionQuestionToken:
                return OperatorPrecedence.Coalesce;
            case SyntaxKind.BarBarToken:
                return OperatorPrecedence.LogicalOR;
            case SyntaxKind.AmpersandAmpersandToken:
                return OperatorPrecedence.LogicalAND;
            case SyntaxKind.BarToken:
                return OperatorPrecedence.BitwiseOR;
            case SyntaxKind.CaretToken:
                return OperatorPrecedence.BitwiseXOR;
            case SyntaxKind.AmpersandToken:
                return OperatorPrecedence.BitwiseAND;
            case SyntaxKind.EqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsToken:
            case SyntaxKind.EqualsEqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsEqualsToken:
                return OperatorPrecedence.Equality;
            case SyntaxKind.LessThanToken:
            case SyntaxKind.GreaterThanToken:
            case SyntaxKind.LessThanEqualsToken:
            case SyntaxKind.GreaterThanEqualsToken:
            case SyntaxKind.InstanceOfKeyword:
            case SyntaxKind.InKeyword:
            case SyntaxKind.AsKeyword:
            case SyntaxKind.SatisfiesKeyword:
                return OperatorPrecedence.Relational;
            case SyntaxKind.LessThanLessThanToken:
            case SyntaxKind.GreaterThanGreaterThanToken:
            case SyntaxKind.GreaterThanGreaterThanGreaterThanToken:
                return OperatorPrecedence.Shift;
            case SyntaxKind.PlusToken:
            case SyntaxKind.MinusToken:
                return OperatorPrecedence.Additive;
            case SyntaxKind.AsteriskToken:
            case SyntaxKind.SlashToken:
            case SyntaxKind.PercentToken:
                return OperatorPrecedence.Multiplicative;
            case SyntaxKind.AsteriskAsteriskToken:
                return OperatorPrecedence.Exponentiation;
        }
        // -1 is lower than all other precedences.  Returning it will cause binary expression
        // parsing to stop.
        return OperatorPrecedence.Invalid;
    }

    // precedence.go:370-401 — Gets the leftmost expression of an expression, e.g. `a` in
    // `a.b`, `a[b]`, `a++`, `a+b`, `a?b:c`, `a as B`, etc.
    public static Node GetLeftmostExpression(Node node, bool stopAtCallExpressions)
    {
        while (true)
        {
            switch (node.Kind)
            {
                case SyntaxKind.PostfixUnaryExpression:
                    node = node.AsPostfixUnaryExpression().Operand!;
                    continue;
                case SyntaxKind.BinaryExpression:
                    node = node.AsBinaryExpression().Left!;
                    continue;
                case SyntaxKind.ConditionalExpression:
                    node = node.AsConditionalExpression().Condition!;
                    continue;
                case SyntaxKind.TaggedTemplateExpression:
                    node = node.AsTaggedTemplateExpression().Tag!;
                    continue;
                case SyntaxKind.CallExpression:
                    if (stopAtCallExpressions)
                    {
                        return node;
                    }
                    goto case SyntaxKind.AsExpression;
                case SyntaxKind.AsExpression:
                case SyntaxKind.ElementAccessExpression:
                case SyntaxKind.PropertyAccessExpression:
                case SyntaxKind.NonNullExpression:
                case SyntaxKind.PartiallyEmittedExpression:
                case SyntaxKind.SatisfiesExpression:
                    node = node.Expression()!;
                    continue;
            }
            return node;
        }
    }

    // precedence.go:655-717 — Gets the precedence of a TypeNode.
    public static TypePrecedence GetTypeNodePrecedence(Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.ConditionalType:
                return TypePrecedence.Conditional;
            case SyntaxKind.JSDocOptionalType:
            case SyntaxKind.JSDocVariadicType:
                return TypePrecedence.JSDoc;
            case SyntaxKind.FunctionType:
            case SyntaxKind.ConstructorType:
                return TypePrecedence.Function;
            case SyntaxKind.UnionType:
                return TypePrecedence.Union;
            case SyntaxKind.IntersectionType:
                return TypePrecedence.Intersection;
            case SyntaxKind.TypeOperator:
                return TypePrecedence.TypeOperator;
            case SyntaxKind.InferType:
                if (n.AsInferTypeNode().TypeParameter!.AsTypeParameterDeclaration().Constraint != null)
                {
                    // `infer T extends U` must be treated as FunctionTypeNode precedence as the `extends` clause eagerly consumes
                    // TypeNode
                    return TypePrecedence.Function;
                }
                return TypePrecedence.TypeOperator;
            case SyntaxKind.IndexedAccessType:
            case SyntaxKind.ArrayType:
            case SyntaxKind.OptionalType:
                return TypePrecedence.Postfix;
            case SyntaxKind.TypeQuery:
                // TypeQueryNode is actually a NonArrayType, but we treat it as TypeOperatorNode
                // precedence so that it is parenthesized when used in a PostfixType
                // context (e.g., `(typeof C)[]` instead of `typeof C[]`)
                return TypePrecedence.TypeOperator;
            case SyntaxKind.AnyKeyword:
            case SyntaxKind.UnknownKeyword:
            case SyntaxKind.StringKeyword:
            case SyntaxKind.NumberKeyword:
            case SyntaxKind.BigIntKeyword:
            case SyntaxKind.SymbolKeyword:
            case SyntaxKind.BooleanKeyword:
            case SyntaxKind.UndefinedKeyword:
            case SyntaxKind.NeverKeyword:
            case SyntaxKind.ObjectKeyword:
            case SyntaxKind.IntrinsicKeyword:
            case SyntaxKind.VoidKeyword:
            case SyntaxKind.JSDocAllType:
            case SyntaxKind.JSDocNullableType:
            case SyntaxKind.JSDocNonNullableType:
            case SyntaxKind.LiteralType:
            case SyntaxKind.TypePredicate:
            case SyntaxKind.TypeReference:
            case SyntaxKind.TypeLiteral:
            case SyntaxKind.TupleType:
            case SyntaxKind.RestType:
            case SyntaxKind.ParenthesizedType:
            case SyntaxKind.ThisType:
            case SyntaxKind.MappedType:
            case SyntaxKind.NamedTupleMember:
            case SyntaxKind.TemplateLiteralType:
            case SyntaxKind.ImportType:
            // These occur in pseudo-types like `f<T>.C`, where `f` is a generic function and `C` is a local type
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ExpressionWithTypeArguments:
                return TypePrecedence.NonArray;
            default:
                throw new InvalidOperationException($"unhandled TypeNode: {n.Kind}");
        }
    }
}
