using System;
using Ts.Core;
using static Ts.Ast.NodeTests;

namespace Ts.Ast;

// Go: internal/ast/utilities.go — the hand-written ast utility surface (IsXxx predicates,
// node navigation, modifier/flag helpers). Ported in Go source order.
//
// PORT-NOTE: Go's package-unqualified generated guards (IsBinaryExpression, IsLiteralKind,
// IsAssignmentOperator, …) resolve to Ts.Ast.NodeTests here via `using static`, keeping the
// Go call form. The nil-tolerant node accessors (Text/Expression/Name/…) are extension
// methods (Node.Accessors.cs); Go's implicit non-nil derefs become `!` (NRE ↔ Go panic,
// plan/05 §4.3).
//
// SCOPE (deferred to the phase-3/4 completion of utilities.go): everything from ~line 1053
// onward (CanHaveIllegalDecorators, declaration-name helpers, comment/JSDoc navigation,
// type-node navigation, GetAssignmentDeclarationKind, …) plus the Symbol/SymbolTable-map
// helpers (GetSymbolTable/GetMembers/GetExports/GetLocals, utilities.go:46-63) and any
// function reading the not-yet-ported Symbol field set — those land with the binder (phase 4).
public static partial class AstUtilities
{
    // GetNodeId / GetSymbolId live in Ids.cs (same partial class).

    // utilities.go:66-68
    public static bool NodeIsMissing(Node? node)
    {
        return node == null || (node.Loc.Pos == node.Loc.End && node.Loc.Pos >= 0 && node.Kind != SyntaxKind.EndOfFile);
    }

    // utilities.go:71-73
    public static bool NodeIsPresent(Node? node)
    {
        return !NodeIsMissing(node);
    }

    // utilities.go:76-78
    public static bool NodeIsSynthesized(Node node)
    {
        return PositionIsSynthesized(node.Loc.Pos) || PositionIsSynthesized(node.Loc.End);
    }

    // utilities.go:80-82
    public static bool RangeIsSynthesized(TextRange loc)
    {
        return PositionIsSynthesized(loc.Pos) || PositionIsSynthesized(loc.End);
    }

    // utilities.go:85-87
    public static bool PositionIsSynthesized(int pos)
    {
        return pos < 0;
    }

    // utilities.go:89-98
    public static Node? FindLastVisibleNode(Node[] nodes)
    {
        int fromEnd = 1;
        while (fromEnd <= nodes.Length && (nodes[nodes.Length - fromEnd].Flags & NodeFlags.Reparsed) != 0)
        {
            fromEnd++;
        }
        if (fromEnd <= nodes.Length)
        {
            return nodes[nodes.Length - fromEnd];
        }
        return null;
    }

    // utilities.go:100-102
    public static bool NodeKindIs(Node node, params SyntaxKind[] kinds)
    {
        return Array.IndexOf(kinds, node.Kind) >= 0;
    }

    // utilities.go:104-106
    public static bool IsModifier(Node node)
    {
        return IsModifierKind(node.Kind);
    }

    // utilities.go:108-110
    public static bool IsModifierLike(Node node)
    {
        return IsModifier(node) || IsDecorator(node);
    }

    // utilities.go:112-114
    public static bool IsCompoundAssignment(SyntaxKind token)
    {
        return token >= SyntaxKind.FirstCompoundAssignment && token <= SyntaxKind.LastCompoundAssignment;
    }

    // utilities.go:116-123
    public static bool IsAssignmentExpression(Node node, bool excludeCompoundAssignment)
    {
        if (node.Kind == SyntaxKind.BinaryExpression)
        {
            var expr = node.AsBinaryExpression();
            return (expr.OperatorToken!.Kind == SyntaxKind.EqualsToken || (!excludeCompoundAssignment && IsAssignmentOperator(expr.OperatorToken.Kind))) &&
                IsLeftHandSideExpression(expr.Left!);
        }
        return false;
    }

    // utilities.go:125-130
    public static Node GetRightMostAssignedExpression(Node node)
    {
        while (IsAssignmentExpression(node, false /*excludeCompoundAssignment*/))
        {
            node = node.AsBinaryExpression().Right!;
        }
        return node;
    }

    // utilities.go:132-138
    public static bool IsDestructuringAssignment(Node node)
    {
        if (IsAssignmentExpression(node, true /*excludeCompoundAssignment*/))
        {
            var kind = node.AsBinaryExpression().Left!.Kind;
            return kind == SyntaxKind.ObjectLiteralExpression || kind == SyntaxKind.ArrayLiteralExpression;
        }
        return false;
    }

    // utilities.go:140-149
    public static bool IsObjectBindingOrAssignmentElement(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.BindingElement:
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.ShorthandPropertyAssignment:
            case SyntaxKind.SpreadAssignment:
                return true;
        }
        return false;
    }

    // utilities.go:151-164
    public static bool IsArrayBindingOrAssignmentElement(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.BindingElement:
            case SyntaxKind.OmittedExpression:
            case SyntaxKind.SpreadElement:
            case SyntaxKind.ArrayLiteralExpression:
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.Identifier:
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ElementAccessExpression:
                return true;
        }
        return IsAssignmentExpression(node, true /*excludeCompoundAssignment*/);
    }

    // utilities.go:166-168
    public static bool IsBindingPattern(Node? node)
    {
        return node != null && (node.Kind == SyntaxKind.ObjectBindingPattern || node.Kind == SyntaxKind.ArrayBindingPattern);
    }

    // utilities.go:170-172
    public static bool IsForInOrOfStatement(Node? node)
    {
        return node != null && (node.Kind == SyntaxKind.ForInStatement || node.Kind == SyntaxKind.ForOfStatement);
    }

    // utilities.go:178-180
    public static bool IsAssignmentTarget(Node node)
    {
        return GetAssignmentTarget(node) != null;
    }

    // utilities.go:184-226
    public static Node? GetAssignmentTarget(Node node)
    {
        while (true)
        {
            var parent = node.Parent;
            switch (parent!.Kind)
            {
                case SyntaxKind.BinaryExpression:
                    if (IsAssignmentOperator(parent.AsBinaryExpression().OperatorToken!.Kind) && parent.AsBinaryExpression().Left == node)
                    {
                        return parent;
                    }
                    return null;
                case SyntaxKind.PrefixUnaryExpression:
                    if (parent.AsPrefixUnaryExpression().Operator == SyntaxKind.PlusPlusToken || parent.AsPrefixUnaryExpression().Operator == SyntaxKind.MinusMinusToken)
                    {
                        return parent;
                    }
                    return null;
                case SyntaxKind.PostfixUnaryExpression:
                    if (parent.AsPostfixUnaryExpression().Operator == SyntaxKind.PlusPlusToken || parent.AsPostfixUnaryExpression().Operator == SyntaxKind.MinusMinusToken)
                    {
                        return parent;
                    }
                    return null;
                case SyntaxKind.ForInStatement:
                case SyntaxKind.ForOfStatement:
                    if (parent.Initializer() == node)
                    {
                        return parent;
                    }
                    return null;
                case SyntaxKind.ParenthesizedExpression:
                case SyntaxKind.ArrayLiteralExpression:
                case SyntaxKind.SpreadElement:
                case SyntaxKind.NonNullExpression:
                    node = parent;
                    break;
                case SyntaxKind.SpreadAssignment:
                    node = parent.Parent!;
                    break;
                case SyntaxKind.ShorthandPropertyAssignment:
                    if (parent.AsShorthandPropertyAssignment().Name() != node)
                    {
                        return null;
                    }
                    node = parent.Parent!;
                    break;
                case SyntaxKind.PropertyAssignment:
                    if (parent.AsPropertyAssignment().Name() == node)
                    {
                        return null;
                    }
                    node = parent.Parent!;
                    break;
                default:
                    return null;
            }
        }
    }

    // utilities.go:228-230
    public static bool IsLogicalBinaryOperator(SyntaxKind token)
    {
        return token == SyntaxKind.BarBarToken || token == SyntaxKind.AmpersandAmpersandToken;
    }

    // utilities.go:232-234
    public static bool IsLogicalOrCoalescingBinaryOperator(SyntaxKind token)
    {
        return IsLogicalBinaryOperator(token) || token == SyntaxKind.QuestionQuestionToken;
    }

    // utilities.go:236-238
    public static bool IsLogicalOrCoalescingBinaryExpression(Node expr)
    {
        return IsBinaryExpression(expr) && IsLogicalOrCoalescingBinaryOperator(expr.AsBinaryExpression().OperatorToken!.Kind);
    }

    // utilities.go:240-242
    public static bool IsLogicalOrCoalescingAssignmentExpression(Node expr)
    {
        return IsBinaryExpression(expr) && IsLogicalOrCoalescingAssignmentOperator(expr.AsBinaryExpression().OperatorToken!.Kind);
    }

    // utilities.go:244-254
    public static bool IsLogicalExpression(Node node)
    {
        while (true)
        {
            if (node.Kind == SyntaxKind.ParenthesizedExpression)
            {
                node = node.Expression()!;
            }
            else if (node.Kind == SyntaxKind.PrefixUnaryExpression && node.AsPrefixUnaryExpression().Operator == SyntaxKind.ExclamationToken)
            {
                node = node.AsPrefixUnaryExpression().Operand!;
            }
            else
            {
                return IsLogicalOrCoalescingBinaryExpression(node);
            }
        }
    }

    // utilities.go:256-258
    public static bool IsAccessor(Node node)
    {
        return node.Kind == SyntaxKind.GetAccessor || node.Kind == SyntaxKind.SetAccessor;
    }

    // utilities.go:260-269
    public static bool IsPropertyNameLiteral(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.NumericLiteral:
                return true;
        }
        return false;
    }

    // utilities.go:271-273
    public static bool IsMemberName(Node node)
    {
        return node.Kind == SyntaxKind.Identifier || node.Kind == SyntaxKind.PrivateIdentifier;
    }

    // utilities.go:275-277
    public static bool IsEntityName(Node node)
    {
        return node.Kind == SyntaxKind.Identifier || node.Kind == SyntaxKind.QualifiedName;
    }

    // utilities.go:279-289
    public static bool IsPropertyName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.ComputedPropertyName:
                return true;
        }
        return false;
    }

    // utilities.go:292-308
    public static bool IsIdentifierName(Node node)
    {
        var parent = node.Parent;
        switch (parent!.Kind)
        {
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.EnumMember:
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.PropertyAccessExpression:
                return parent.Name() == node;
            case SyntaxKind.QualifiedName:
                return parent.AsQualifiedName().Right == node;
            case SyntaxKind.BindingElement:
                return parent.PropertyName() == node;
            case SyntaxKind.ImportSpecifier:
                return parent.PropertyName() == node;
            case SyntaxKind.ExportSpecifier:
            case SyntaxKind.JsxAttribute:
            case SyntaxKind.JsxSelfClosingElement:
            case SyntaxKind.JsxOpeningElement:
            case SyntaxKind.JsxClosingElement:
                return true;
        }
        return false;
    }

    // utilities.go:310-313
    public static bool IsPushOrUnshiftIdentifier(Node node)
    {
        var text = node.Text();
        return text == "push" || text == "unshift";
    }

    // utilities.go:315-317
    public static bool IsBooleanLiteral(Node node)
    {
        return node.Kind == SyntaxKind.TrueKeyword || node.Kind == SyntaxKind.FalseKeyword;
    }

    // utilities.go:319-321
    public static bool IsLiteralExpression(Node node)
    {
        return IsLiteralKind(node.Kind);
    }

    // utilities.go:323-329
    public static bool IsStringLiteralLike(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                return true;
        }
        return false;
    }

    // utilities.go:331-333
    public static bool IsStringOrNumericLiteralLike(Node node)
    {
        return IsStringLiteralLike(node) || IsNumericLiteral(node);
    }

    // utilities.go:335-341
    public static bool IsSignedNumericLiteral(Node node)
    {
        if (node.Kind == SyntaxKind.PrefixUnaryExpression)
        {
            var n = node.AsPrefixUnaryExpression();
            return (n.Operator == SyntaxKind.PlusToken || n.Operator == SyntaxKind.MinusToken) && IsNumericLiteral(n.Operand!);
        }
        return false;
    }

    // utilities.go:344-355
    public static bool IsOptionalChain(Node node)
    {
        if ((node.Flags & NodeFlags.OptionalChain) != 0)
        {
            switch (node.Kind)
            {
                case SyntaxKind.PropertyAccessExpression:
                case SyntaxKind.ElementAccessExpression:
                case SyntaxKind.CallExpression:
                case SyntaxKind.NonNullExpression:
                    return true;
            }
        }
        return false;
    }

    // utilities.go:357-359
    private static Node? GetQuestionDotToken(Node node)
    {
        return node.QuestionDotToken();
    }

    // utilities.go:362-364
    public static bool IsOptionalChainRoot(Node node)
    {
        return IsOptionalChain(node) && !IsNonNullExpression(node) && GetQuestionDotToken(node) != null;
    }

    // utilities.go:375-380
    public static bool IsOutermostOptionalChain(Node node)
    {
        var parent = node.Parent!;
        return !IsOptionalChain(parent) || // cases 1, 2, and 3
            IsOptionalChainRoot(parent) || // case 4
            node != parent.Expression(); // case 5
    }

    // utilities.go:383-385
    public static bool IsExpressionOfOptionalChainRoot(Node node)
    {
        return IsOptionalChainRoot(node.Parent!) && node.Parent!.Expression() == node;
    }

    // utilities.go:387-389
    public static bool IsNullishCoalesce(Node node)
    {
        return node.Kind == SyntaxKind.BinaryExpression && node.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.QuestionQuestionToken;
    }

    // utilities.go:391-394
    public static bool IsAssertionExpression(Node node)
    {
        var kind = node.Kind;
        return kind == SyntaxKind.TypeAssertionExpression || kind == SyntaxKind.AsExpression;
    }

    // utilities.go:396-408
    private static bool IsLeftHandSideExpressionKind(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ElementAccessExpression:
            case SyntaxKind.NewExpression:
            case SyntaxKind.CallExpression:
            case SyntaxKind.JsxElement:
            case SyntaxKind.JsxSelfClosingElement:
            case SyntaxKind.JsxFragment:
            case SyntaxKind.TaggedTemplateExpression:
            case SyntaxKind.ArrayLiteralExpression:
            case SyntaxKind.ParenthesizedExpression:
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
            case SyntaxKind.RegularExpressionLiteral:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.TemplateExpression:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.NullKeyword:
            case SyntaxKind.ThisKeyword:
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.SuperKeyword:
            case SyntaxKind.NonNullExpression:
            case SyntaxKind.ExpressionWithTypeArguments:
            case SyntaxKind.MetaProperty:
            case SyntaxKind.ImportKeyword:
            case SyntaxKind.MissingDeclaration:
                return true;
        }
        return false;
    }

    // utilities.go:411-413
    public static bool IsLeftHandSideExpression(Node node)
    {
        return IsLeftHandSideExpressionKind(SkipPartiallyEmittedExpressions(node).Kind);
    }

    // utilities.go:415-427
    private static bool IsUnaryExpressionKind(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.PrefixUnaryExpression:
            case SyntaxKind.PostfixUnaryExpression:
            case SyntaxKind.DeleteExpression:
            case SyntaxKind.TypeOfExpression:
            case SyntaxKind.VoidExpression:
            case SyntaxKind.AwaitExpression:
            case SyntaxKind.TypeAssertionExpression:
                return true;
        }
        return IsLeftHandSideExpressionKind(kind);
    }

    // utilities.go:430-432
    public static bool IsUnaryExpression(Node node)
    {
        return IsUnaryExpressionKind(SkipPartiallyEmittedExpressions(node).Kind);
    }

    // utilities.go:434-448
    private static bool IsExpressionKind(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.ConditionalExpression:
            case SyntaxKind.YieldExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.BinaryExpression:
            case SyntaxKind.SpreadElement:
            case SyntaxKind.AsExpression:
            case SyntaxKind.OmittedExpression:
            case SyntaxKind.PartiallyEmittedExpression:
            case SyntaxKind.SatisfiesExpression:
                return true;
        }
        return IsUnaryExpressionKind(kind);
    }

    // utilities.go:451-453
    public static bool IsExpression(Node node)
    {
        return IsExpressionKind(SkipPartiallyEmittedExpressions(node).Kind);
    }

    // utilities.go:1319-1326
    public static bool IsJsxTagName(Node node)
    {
        var parent = node.Parent;
        switch (parent!.Kind)
        {
            case SyntaxKind.JsxOpeningElement:
            case SyntaxKind.JsxClosingElement:
            case SyntaxKind.JsxSelfClosingElement:
                return parent.TagName() == node;
        }
        return false;
    }

    // utilities.go:1929-1961
    public static bool IsExpressionNode(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.SuperKeyword:
            case SyntaxKind.NullKeyword:
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.RegularExpressionLiteral:
            case SyntaxKind.ArrayLiteralExpression:
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ElementAccessExpression:
            case SyntaxKind.CallExpression:
            case SyntaxKind.NewExpression:
            case SyntaxKind.TaggedTemplateExpression:
            case SyntaxKind.AsExpression:
            case SyntaxKind.TypeAssertionExpression:
            case SyntaxKind.SatisfiesExpression:
            case SyntaxKind.NonNullExpression:
            case SyntaxKind.ParenthesizedExpression:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.VoidExpression:
            case SyntaxKind.DeleteExpression:
            case SyntaxKind.TypeOfExpression:
            case SyntaxKind.PrefixUnaryExpression:
            case SyntaxKind.PostfixUnaryExpression:
            case SyntaxKind.BinaryExpression:
            case SyntaxKind.ConditionalExpression:
            case SyntaxKind.SpreadElement:
            case SyntaxKind.TemplateExpression:
            case SyntaxKind.OmittedExpression:
            case SyntaxKind.JsxElement:
            case SyntaxKind.JsxSelfClosingElement:
            case SyntaxKind.JsxFragment:
            case SyntaxKind.YieldExpression:
            case SyntaxKind.AwaitExpression:
                return true;
            case SyntaxKind.MetaProperty:
                // `import.defer` in `import.defer(...)` is not an expression
                return !IsImportCall(node.Parent!) || node.Parent!.Expression() != node;
            case SyntaxKind.ExpressionWithTypeArguments:
                return !IsHeritageClause(node.Parent!);
            case SyntaxKind.QualifiedName:
                while (node.Parent!.Kind == SyntaxKind.QualifiedName)
                {
                    node = node.Parent;
                }
                return IsTypeQueryNode(node.Parent) || IsJSDocLinkLike(node.Parent) || IsJSDocNameReference(node.Parent) || IsJsxTagName(node);
            case SyntaxKind.PrivateIdentifier:
                return IsBinaryExpression(node.Parent!) && node.Parent!.AsBinaryExpression().Left == node && node.Parent!.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.InKeyword;
            case SyntaxKind.Identifier:
                if (IsTypeQueryNode(node.Parent!) || IsJSDocLinkLike(node.Parent!) || IsJSDocNameReference(node.Parent!) || IsJsxTagName(node))
                {
                    return true;
                }
                goto case SyntaxKind.ThisKeyword;
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.ThisKeyword:
                return IsInExpressionContext(node);
            default:
                return false;
        }
    }

    // utilities.go:1963-1988
    public static bool IsInExpressionContext(Node node)
    {
        var parent = node.Parent;
        switch (parent!.Kind)
        {
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.Parameter:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.EnumMember:
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.BindingElement:
                return parent.Initializer() == node;
            case SyntaxKind.ExpressionStatement:
            case SyntaxKind.IfStatement:
            case SyntaxKind.DoStatement:
            case SyntaxKind.WhileStatement:
            case SyntaxKind.ReturnStatement:
            case SyntaxKind.WithStatement:
            case SyntaxKind.SwitchStatement:
            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
            case SyntaxKind.ThrowStatement:
            case SyntaxKind.TypeAssertionExpression:
            case SyntaxKind.AsExpression:
            case SyntaxKind.TemplateSpan:
            case SyntaxKind.ComputedPropertyName:
            case SyntaxKind.SatisfiesExpression:
                return parent.Expression() == node;
            case SyntaxKind.ForStatement:
                {
                    var s = parent.AsForStatement();
                    return s.Initializer == node && s.Initializer!.Kind != SyntaxKind.VariableDeclarationList || s.Condition == node || s.Incrementor == node;
                }
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
                {
                    var s = parent.AsForInOrOfStatement();
                    return s.Initializer == node && s.Initializer!.Kind != SyntaxKind.VariableDeclarationList || s.Expression == node;
                }
            case SyntaxKind.Decorator:
            case SyntaxKind.JsxExpression:
            case SyntaxKind.JsxSpreadAttribute:
            case SyntaxKind.SpreadAssignment:
                return true;
            case SyntaxKind.ExpressionWithTypeArguments:
                return parent.Expression() == node && !IsPartOfTypeNode(parent);
            case SyntaxKind.ShorthandPropertyAssignment:
                return parent.AsShorthandPropertyAssignment().ObjectAssignmentInitializer == node;
            default:
                return IsExpressionNode(parent);
        }
    }

    // utilities.go:1990-2019
    public static bool IsPartOfTypeNode(Node node)
    {
        var kind = node.Kind;
        if (kind >= SyntaxKind.FirstTypeNode && kind <= SyntaxKind.LastTypeNode)
        {
            return true;
        }
        switch (node.Kind)
        {
            case SyntaxKind.AnyKeyword:
            case SyntaxKind.UnknownKeyword:
            case SyntaxKind.NumberKeyword:
            case SyntaxKind.BigIntKeyword:
            case SyntaxKind.StringKeyword:
            case SyntaxKind.BooleanKeyword:
            case SyntaxKind.SymbolKeyword:
            case SyntaxKind.ObjectKeyword:
            case SyntaxKind.UndefinedKeyword:
            case SyntaxKind.NullKeyword:
            case SyntaxKind.NeverKeyword:
                return true;
            case SyntaxKind.VoidKeyword:
                return node.Parent!.Kind != SyntaxKind.VoidExpression;
            case SyntaxKind.ExpressionWithTypeArguments:
                return IsPartOfTypeExpressionWithTypeArguments(node);
            case SyntaxKind.TypeParameter:
                return node.Parent!.Kind == SyntaxKind.MappedType || node.Parent!.Kind == SyntaxKind.InferType;
            case SyntaxKind.Identifier:
                {
                    var parent = node.Parent;
                    if (IsQualifiedName(parent!) && parent!.AsQualifiedName().Right == node)
                    {
                        return IsPartOfTypeNodeInParent(parent);
                    }
                    if (IsPropertyAccessExpression(parent!) && parent!.AsPropertyAccessExpression().Name() == node)
                    {
                        return IsPartOfTypeNodeInParent(parent);
                    }
                    return IsPartOfTypeNodeInParent(node);
                }
            case SyntaxKind.QualifiedName:
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ThisKeyword:
                return IsPartOfTypeNodeInParent(node);
        }
        return false;
    }

    // utilities.go:2021-2053
    private static bool IsPartOfTypeNodeInParent(Node node)
    {
        var parent = node.Parent;
        if (parent!.Kind == SyntaxKind.TypeQuery)
        {
            return false;
        }
        if (parent.Kind == SyntaxKind.ImportType)
        {
            return !parent.AsImportTypeNode().IsTypeOf;
        }

        // Do not recursively call isPartOfTypeNode on the parent. In the example:
        //
        //     let a: A.B.C;
        //
        // Calling isPartOfTypeNode would consider the qualified name A.B a type node.
        // Only C and A.B.C are type nodes.
        if (parent.Kind >= SyntaxKind.FirstTypeNode && parent.Kind <= SyntaxKind.LastTypeNode)
        {
            return true;
        }
        switch (parent.Kind)
        {
            case SyntaxKind.ExpressionWithTypeArguments:
                return IsPartOfTypeExpressionWithTypeArguments(parent);
            case SyntaxKind.TypeParameter:
                return node == parent.AsTypeParameterDeclaration().Constraint;
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.Parameter:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.Constructor:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.CallSignature:
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.IndexSignature:
            case SyntaxKind.TypeAssertionExpression:
                return node == parent.Type();
            case SyntaxKind.CallExpression:
            case SyntaxKind.NewExpression:
            case SyntaxKind.TaggedTemplateExpression:
                {
                    var typeArguments = parent.TypeArguments();
                    return typeArguments != null && Array.IndexOf(typeArguments, node) >= 0;
                }
        }
        return false;
    }

    // utilities.go:2055-2060
    private static bool IsPartOfTypeExpressionWithTypeArguments(Node node)
    {
        var parent = node.Parent;
        return IsHeritageClause(parent!) && (!IsClassLike(parent!.Parent!) || parent.AsHeritageClause().Token == SyntaxKind.ImplementsKeyword) ||
            IsJSDocImplementsTag(parent!) ||
            IsJSDocAugmentsTag(parent!);
    }

    // utilities.go:2074-2080
    public static bool IsImportCall(Node node)
    {
        if (!IsCallExpression(node))
        {
            return false;
        }
        var e = node.Expression()!;
        return e.Kind == SyntaxKind.ImportKeyword || IsMetaProperty(e) && e.AsMetaProperty().KeywordToken == SyntaxKind.ImportKeyword && e.Text() == "defer";
    }

    // utilities.go:2240-2242
    public static bool IsPropertyAccessOrQualifiedName(Node node)
    {
        return node.Kind == SyntaxKind.PropertyAccessExpression || node.Kind == SyntaxKind.QualifiedName;
    }

    // ast.go:1901-1903
    public static bool IsAnyExportAssignment(Node node)
    {
        return node.Kind == SyntaxKind.ExportAssignment;
    }

    // utilities.go:4200-4207
    public static Node? GetFirstConstructorWithBody(Node node)
    {
        foreach (var member in node.Members() ?? [])
        {
            if (IsConstructorDeclaration(member) && NodeIsPresent(member.Body()))
            {
                return member;
            }
        }
        return null;
    }

    // utilities.go:4432-4443
    public static Node? GetRestParameterElementType(Node? node)
    {
        if (node == null)
        {
            return node;
        }
        if (node.Kind == SyntaxKind.ArrayType)
        {
            return node.AsArrayTypeNode().ElementType;
        }
        if (node.Kind == SyntaxKind.TypeReference && node.AsTypeReferenceNode().TypeArguments != null)
        {
            return CoreUtil.FirstOrNil(node.AsTypeReferenceNode().TypeArguments!.Nodes);
        }
        return null;
    }

    // utilities.go:455-457
    public static bool IsCommaExpression(Node node)
    {
        return node.Kind == SyntaxKind.BinaryExpression && node.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.CommaToken;
    }

    // utilities.go:459-461
    public static bool IsCommaSequence(Node node)
    {
        return IsCommaExpression(node);
    }

    // utilities.go:463-476
    public static bool IsIterationStatement(Node node, bool lookInLabeledStatements)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ForStatement:
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
            case SyntaxKind.DoStatement:
            case SyntaxKind.WhileStatement:
                return true;
            case SyntaxKind.LabeledStatement:
                return lookInLabeledStatements && IsIterationStatement(node.Statement()!, lookInLabeledStatements);
        }
        return false;
    }

    // utilities.go:479-481
    public static bool IsAccessExpression(Node node)
    {
        return node.Kind == SyntaxKind.PropertyAccessExpression || node.Kind == SyntaxKind.ElementAccessExpression;
    }

    // utilities.go:483-495
    private static bool IsFunctionLikeDeclarationKind(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.Constructor:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
                return true;
        }
        return false;
    }

    // utilities.go:498-501
    public static bool IsFunctionLikeDeclaration(Node? node)
    {
        // TODO(rbuckton): Move `node != nil` test to call sites
        return node != null && IsFunctionLikeDeclarationKind(node.Kind);
    }

    // utilities.go:503-515
    public static bool IsFunctionLikeKind(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.MethodSignature:
            case SyntaxKind.CallSignature:
            case SyntaxKind.JSDocSignature:
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.IndexSignature:
            case SyntaxKind.FunctionType:
            case SyntaxKind.ConstructorType:
                return true;
        }
        return IsFunctionLikeDeclarationKind(kind);
    }

    // utilities.go:518-521
    public static bool IsFunctionLike(Node? node)
    {
        // TODO(rbuckton): Move `node != nil` test to call sites
        return node != null && IsFunctionLikeKind(node.Kind);
    }

    // utilities.go:523-525
    public static bool IsFunctionLikeOrClassStaticBlockDeclaration(Node? node)
    {
        return node != null && (IsFunctionLike(node) || IsClassStaticBlockDeclaration(node));
    }

    // utilities.go:527-529
    public static bool IsFunctionOrSourceFile(Node node)
    {
        return IsFunctionLike(node) || IsSourceFile(node);
    }

    // utilities.go:531-533
    public static bool IsClassLike(Node node)
    {
        return node.Kind == SyntaxKind.ClassDeclaration || node.Kind == SyntaxKind.ClassExpression;
    }

    // utilities.go:535-537
    public static bool IsClassOrInterfaceLike(Node node)
    {
        return node.Kind == SyntaxKind.ClassDeclaration || node.Kind == SyntaxKind.ClassExpression || node.Kind == SyntaxKind.InterfaceDeclaration;
    }

    // utilities.go:3009-3016
    public static bool IsVariableLike(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.BindingElement:
            case SyntaxKind.EnumMember:
            case SyntaxKind.Parameter:
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.ShorthandPropertyAssignment:
            case SyntaxKind.VariableDeclaration:
                return true;
        }
        return false;
    }

    // utilities.go:539-552
    public static bool IsClassElement(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Constructor:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.IndexSignature:
            case SyntaxKind.ClassStaticBlockDeclaration:
            case SyntaxKind.SemicolonClassElement:
                return true;
        }
        return false;
    }

    // utilities.go:554-560
    public static bool IsMethodOrAccessor(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                return true;
        }
        return false;
    }

    // utilities.go:562-564
    public static bool IsPrivateIdentifierClassElementDeclaration(Node node)
    {
        return (IsPropertyDeclaration(node) || IsMethodOrAccessor(node)) && IsPrivateIdentifier(node.Name()!);
    }

    // utilities.go:566-570
    public static bool IsObjectLiteralOrClassExpressionMethodOrAccessor(Node node)
    {
        var kind = node.Kind;
        return (kind == SyntaxKind.MethodDeclaration || kind == SyntaxKind.GetAccessor || kind == SyntaxKind.SetAccessor) &&
            (node.Parent!.Kind == SyntaxKind.ObjectLiteralExpression || node.Parent.Kind == SyntaxKind.ClassExpression);
    }

    // utilities.go:572-585
    public static bool IsTypeElement(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.CallSignature:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.IndexSignature:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.NotEmittedTypeElement:
                return true;
        }
        return false;
    }

    // utilities.go:587-598
    public static bool IsObjectLiteralElement(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.ShorthandPropertyAssignment:
            case SyntaxKind.SpreadAssignment:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                return true;
        }
        return false;
    }

    // utilities.go:600-602
    public static bool IsObjectLiteralMethod(Node? node)
    {
        return node != null && node.Kind == SyntaxKind.MethodDeclaration && node.Parent!.Kind == SyntaxKind.ObjectLiteralExpression;
    }

    // utilities.go:604-606
    public static bool IsAutoAccessorPropertyDeclaration(Node node)
    {
        return IsPropertyDeclaration(node) && HasAccessorModifier(node);
    }

    // utilities.go:608-610
    public static bool IsParameterPropertyDeclaration(Node node, Node parent)
    {
        return IsParameterDeclaration(node) && HasSyntacticModifier(node, ModifierFlags.ParameterPropertyModifier) && parent.Kind == SyntaxKind.Constructor;
    }

    // utilities.go:612-622
    public static bool IsJsxChild(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.JsxElement:
            case SyntaxKind.JsxExpression:
            case SyntaxKind.JsxSelfClosingElement:
            case SyntaxKind.JsxText:
            case SyntaxKind.JsxFragment:
                return true;
        }
        return false;
    }

    // utilities.go:624-626
    public static bool IsJsxAttributeLike(Node node)
    {
        return IsJsxAttribute(node) || IsJsxSpreadAttribute(node);
    }

    // utilities.go:628-647
    private static bool IsDeclarationStatementKind(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.MissingDeclaration:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.ExportDeclaration:
            case SyntaxKind.ExportAssignment:
            case SyntaxKind.NamespaceExportDeclaration:
                return true;
        }
        return false;
    }

    // utilities.go:653-655
    public static bool IsDeclarationStatement(Node node)
    {
        return IsDeclarationStatementKind(node.Kind);
    }

    // utilities.go:657-681
    private static bool IsStatementKindButNotDeclarationKind(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.BreakStatement:
            case SyntaxKind.ContinueStatement:
            case SyntaxKind.DebuggerStatement:
            case SyntaxKind.DoStatement:
            case SyntaxKind.ExpressionStatement:
            case SyntaxKind.EmptyStatement:
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
            case SyntaxKind.ForStatement:
            case SyntaxKind.IfStatement:
            case SyntaxKind.LabeledStatement:
            case SyntaxKind.ReturnStatement:
            case SyntaxKind.SwitchStatement:
            case SyntaxKind.ThrowStatement:
            case SyntaxKind.TryStatement:
            case SyntaxKind.VariableStatement:
            case SyntaxKind.WhileStatement:
            case SyntaxKind.WithStatement:
            case SyntaxKind.NotEmittedStatement:
                return true;
        }
        return false;
    }

    // utilities.go:687-689
    public static bool IsStatementButNotDeclaration(Node node)
    {
        return IsStatementKindButNotDeclarationKind(node.Kind);
    }

    // utilities.go:695-698
    public static bool IsStatement(Node node)
    {
        var kind = node.Kind;
        return IsStatementKindButNotDeclarationKind(kind) || IsDeclarationStatementKind(kind) || IsBlockStatement(node);
    }

    // utilities.go:702-710
    private static bool IsBlockStatement(Node node)
    {
        if (node.Kind != SyntaxKind.Block)
        {
            return false;
        }
        if (node.Parent != null && (node.Parent.Kind == SyntaxKind.TryStatement || node.Parent.Kind == SyntaxKind.CatchClause))
        {
            return false;
        }
        return !IsFunctionBlock(node);
    }

    // utilities.go:713-715
    public static bool IsFunctionBlock(Node? node)
    {
        return node != null && node.Kind == SyntaxKind.Block && node.Parent != null && IsFunctionLike(node.Parent);
    }

    // utilities.go:717-719
    public static bool IsBlockOrCatchScoped(Node declaration)
    {
        return (GetCombinedNodeFlags(declaration) & NodeFlags.BlockScoped) != 0 || IsCatchClauseVariableDeclarationOrBindingElement(declaration);
    }

    // utilities.go:721-724
    public static bool IsCatchClauseVariableDeclarationOrBindingElement(Node declaration)
    {
        var node = GetRootDeclaration(declaration);
        return node.Kind == SyntaxKind.VariableDeclaration && node.Parent!.Kind == SyntaxKind.CatchClause;
    }

    // utilities.go:726-749
    public static bool IsTypeNodeKind(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.AnyKeyword:
            case SyntaxKind.UnknownKeyword:
            case SyntaxKind.NumberKeyword:
            case SyntaxKind.BigIntKeyword:
            case SyntaxKind.ObjectKeyword:
            case SyntaxKind.BooleanKeyword:
            case SyntaxKind.StringKeyword:
            case SyntaxKind.SymbolKeyword:
            case SyntaxKind.VoidKeyword:
            case SyntaxKind.UndefinedKeyword:
            case SyntaxKind.NeverKeyword:
            case SyntaxKind.IntrinsicKeyword:
            case SyntaxKind.ExpressionWithTypeArguments:
            case SyntaxKind.JSDocAllType:
            case SyntaxKind.JSDocNullableType:
            case SyntaxKind.JSDocNonNullableType:
            case SyntaxKind.JSDocOptionalType:
            case SyntaxKind.JSDocVariadicType:
                return true;
        }
        return kind >= SyntaxKind.FirstTypeNode && kind <= SyntaxKind.LastTypeNode;
    }

    // utilities.go:751-753
    public static bool IsTypeNode(Node node)
    {
        return IsTypeNodeKind(node.Kind);
    }

    // utilities.go:755-757
    public static bool IsJSDocKind(SyntaxKind kind)
    {
        return SyntaxKind.FirstJSDocNode <= kind && kind <= SyntaxKind.LastJSDocNode;
    }

    // utilities.go:759-765
    public static bool IsJSDocTypeAssertion(Node? node)
    {
        if (node == null || !IsParenthesizedExpression(node) || !IsInJSFile(node))
        {
            return false;
        }
        var expr = node.Expression()!;
        return IsAsExpression(expr) && expr.Type() != null && (expr.Type()!.Flags & NodeFlags.Reparsed) != 0;
    }

    // utilities.go:767-770
    public static bool IsPrologueDirective(Node node)
    {
        return node.Kind == SyntaxKind.ExpressionStatement &&
            node.Expression()!.Kind == SyntaxKind.StringLiteral;
    }

    // utilities.go:791-814
    public static bool IsOuterExpression(Node node, OuterExpressionKinds kinds)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ParenthesizedExpression:
                return (kinds & OuterExpressionKinds.Parentheses) != 0 && !((kinds & OuterExpressionKinds.ExcludeJSDocTypeAssertion) != 0 && IsJSDocTypeAssertion(node));
            case SyntaxKind.TypeAssertionExpression:
            case SyntaxKind.AsExpression:
                return (kinds & OuterExpressionKinds.TypeAssertions) != 0;
            case SyntaxKind.SatisfiesExpression:
                return (kinds & (OuterExpressionKinds.ExpressionsWithTypeArguments | OuterExpressionKinds.Satisfies)) != 0;
            case SyntaxKind.ExpressionWithTypeArguments:
                return (kinds & OuterExpressionKinds.ExpressionsWithTypeArguments) != 0;
            case SyntaxKind.NonNullExpression:
                return (kinds & OuterExpressionKinds.NonNullAssertions) != 0;
            case SyntaxKind.PartiallyEmittedExpression:
                return (kinds & OuterExpressionKinds.PartiallyEmittedExpressions) != 0;
            case SyntaxKind.BinaryExpression:
                switch (node.AsBinaryExpression().OperatorToken!.Kind)
                {
                    case SyntaxKind.EqualsToken:
                        return (kinds & OuterExpressionKinds.Assignments) != 0;
                    case SyntaxKind.CommaToken:
                        return (kinds & OuterExpressionKinds.Comma) != 0;
                }
                break;
        }
        return false;
    }

    // utilities.go:817-826
    public static Node SkipOuterExpressions(Node node, OuterExpressionKinds kinds)
    {
        while (IsOuterExpression(node, kinds))
        {
            if (IsBinaryExpression(node))
            {
                node = node.AsBinaryExpression().Right!;
            }
            else
            {
                node = node.Expression()!;
            }
        }
        return node;
    }

    // utilities.go:829-831
    public static Node SkipParentheses(Node node)
    {
        return SkipOuterExpressions(node, OuterExpressionKinds.Parentheses);
    }

    // utilities.go:833-838
    public static Node SkipTypeParentheses(Node node)
    {
        while (IsParenthesizedTypeNode(node))
        {
            node = node.Type()!;
        }
        return node;
    }

    // utilities.go:840-842
    public static Node SkipPartiallyEmittedExpressions(Node node)
    {
        return SkipOuterExpressions(node, OuterExpressionKinds.PartiallyEmittedExpressions);
    }

    // utilities.go:845-850
    public static Node? WalkUpParenthesizedExpressions(Node? node)
    {
        while (node != null && node.Kind == SyntaxKind.ParenthesizedExpression)
        {
            node = node.Parent;
        }
        return node;
    }

    // utilities.go:853-858
    public static Node? WalkUpParenthesizedTypes(Node? node)
    {
        while (node != null && node.Kind == SyntaxKind.ParenthesizedType)
        {
            node = node.Parent;
        }
        return node;
    }

    // utilities.go:861-869
    public static SourceFile? GetSourceFileOfNode(Node? node)
    {
        while (node != null)
        {
            if (node.Kind == SyntaxKind.SourceFile)
            {
                return node.AsSourceFile();
            }
            node = node.Parent;
        }
        return null;
    }

    // utilities.go:899-903 — PORT-NOTE: sync.Pool dropped (not a plan/01 §5.2 pooled
    // allocator); the recursive setter is allocated per call.
    public static void SetParentInChildren(Node node)
    {
        Node? parent = null;
        Visitor visit = null!;
        visit = child =>
        {
            if (parent != null)
            {
                child.Parent = parent;
            }
            var saveParent = parent;
            parent = child;
            child.ForEachChild(visit);
            parent = saveParent;
            return false;
        };
        visit(node);
    }

    // utilities.go:906-908 — This should never be called outside the parser.
    public static void SetImportsOfSourceFile(SourceFile node, Node[]? imports)
    {
        node.imports = imports;
    }

    // utilities.go:911-919
    public static Node? FindAncestor(Node? node, Func<Node, bool> callback)
    {
        while (node != null)
        {
            if (callback(node))
            {
                return node;
            }
            node = node.Parent;
        }
        return null;
    }

    // utilities.go:922-930
    public static Node? FindAncestorKind(Node? node, SyntaxKind kind)
    {
        while (node != null)
        {
            if (node.Kind == kind)
            {
                return node;
            }
            node = node.Parent;
        }
        return null;
    }

    // utilities.go:940-945
    public static FindAncestorResult ToFindAncestorResult(bool b)
    {
        if (b)
        {
            return FindAncestorResult.True;
        }
        return FindAncestorResult.False;
    }

    // utilities.go:948-959
    public static Node? FindAncestorOrQuit(Node? node, Func<Node, FindAncestorResult> callback)
    {
        while (node != null)
        {
            switch (callback(node))
            {
                case FindAncestorResult.Quit:
                    return null;
                case FindAncestorResult.True:
                    return node;
            }
            node = node.Parent;
        }
        return null;
    }

    // utilities.go:961-969
    public static bool IsNodeDescendantOf(Node? node, Node? ancestor)
    {
        while (node != null)
        {
            if (node == ancestor)
            {
                return true;
            }
            node = node.Parent;
        }
        return false;
    }

    // utilities.go:971-1007
    public static ModifierFlags ModifierToFlag(SyntaxKind token)
    {
        switch (token)
        {
            case SyntaxKind.StaticKeyword:
                return ModifierFlags.Static;
            case SyntaxKind.PublicKeyword:
                return ModifierFlags.Public;
            case SyntaxKind.ProtectedKeyword:
                return ModifierFlags.Protected;
            case SyntaxKind.PrivateKeyword:
                return ModifierFlags.Private;
            case SyntaxKind.AbstractKeyword:
                return ModifierFlags.Abstract;
            case SyntaxKind.AccessorKeyword:
                return ModifierFlags.Accessor;
            case SyntaxKind.ExportKeyword:
                return ModifierFlags.Export;
            case SyntaxKind.DeclareKeyword:
                return ModifierFlags.Ambient;
            case SyntaxKind.ConstKeyword:
                return ModifierFlags.Const;
            case SyntaxKind.DefaultKeyword:
                return ModifierFlags.Default;
            case SyntaxKind.AsyncKeyword:
                return ModifierFlags.Async;
            case SyntaxKind.ReadonlyKeyword:
                return ModifierFlags.Readonly;
            case SyntaxKind.OverrideKeyword:
                return ModifierFlags.Override;
            case SyntaxKind.InKeyword:
                return ModifierFlags.In;
            case SyntaxKind.OutKeyword:
                return ModifierFlags.Out;
            case SyntaxKind.Decorator:
                return ModifierFlags.Decorator;
        }
        return ModifierFlags.None;
    }

    // utilities.go:1009-1015
    public static ModifierFlags ModifiersToFlags(Node[]? modifiers)
    {
        var flags = ModifierFlags.None;
        if (modifiers != null)
        {
            foreach (var modifier in modifiers)
            {
                flags |= ModifierToFlag(modifier.Kind);
            }
        }
        return flags;
    }

    // utilities.go:1017-1019
    public static bool HasSyntacticModifier(Node node, ModifierFlags flags)
    {
        return (node.ModifierFlags() & flags) != 0;
    }

    // utilities.go:1021-1023
    public static bool HasAccessorModifier(Node node)
    {
        return HasSyntacticModifier(node, ModifierFlags.Accessor);
    }

    // utilities.go:1025-1027
    public static bool HasStaticModifier(Node node)
    {
        return HasSyntacticModifier(node, ModifierFlags.Static);
    }

    // utilities.go:1029-1032
    public static bool IsStatic(Node node)
    {
        // https://tc39.es/ecma262/#sec-static-semantics-isstatic
        return (IsClassElement(node) && HasStaticModifier(node)) || IsClassStaticBlockDeclaration(node);
    }

    // utilities.go:1034-1051
    public static bool CanHaveSymbol(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.BinaryExpression:
            case SyntaxKind.BindingElement:
            case SyntaxKind.CallExpression:
            case SyntaxKind.CallSignature:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.ClassStaticBlockDeclaration:
            case SyntaxKind.Constructor:
            case SyntaxKind.ConstructorType:
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.ElementAccessExpression:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.EnumMember:
            case SyntaxKind.ExportAssignment:
            case SyntaxKind.ExportDeclaration:
            case SyntaxKind.ExportSpecifier:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.FunctionType:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.ImportClause:
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.ImportSpecifier:
            case SyntaxKind.IndexSignature:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
            case SyntaxKind.JsxAttribute:
            case SyntaxKind.JsxAttributes:
            case SyntaxKind.JsxSpreadAttribute:
            case SyntaxKind.MappedType:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.NamedTupleMember:
            case SyntaxKind.NamespaceExport:
            case SyntaxKind.NamespaceExportDeclaration:
            case SyntaxKind.NamespaceImport:
            case SyntaxKind.NewExpression:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.Parameter:
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.ShorthandPropertyAssignment:
            case SyntaxKind.SourceFile:
            case SyntaxKind.SpreadAssignment:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.TypeLiteral:
            case SyntaxKind.TypeParameter:
            case SyntaxKind.VariableDeclaration:
                return true;
        }
        return false;
    }

    // utilities.go:1154-1159
    public static Node GetRootDeclaration(Node node)
    {
        while (node.Kind == SyntaxKind.BindingElement)
        {
            node = node.Parent!.Parent!;
        }
        return node;
    }

    // utilities.go:1177-1179 — getCombinedFlags specialized (C# can't `|` a generic enum).
    public static ModifierFlags GetCombinedModifierFlags(Node node)
    {
        node = GetRootDeclaration(node);
        var flags = node.ModifierFlags();
        if (node.Kind == SyntaxKind.VariableDeclaration)
        {
            node = node.Parent!;
        }
        if (node != null && node.Kind == SyntaxKind.VariableDeclarationList)
        {
            flags |= node.ModifierFlags();
            node = node.Parent!;
        }
        if (node != null && node.Kind == SyntaxKind.VariableStatement)
        {
            flags |= node.ModifierFlags();
        }
        return flags;
    }

    // utilities.go:1181-1187
    public static NodeFlags GetCombinedNodeFlags(Node node)
    {
        node = GetRootDeclaration(node);
        var flags = node.Flags;
        if (node.Kind == SyntaxKind.VariableDeclaration)
        {
            node = node.Parent!;
        }
        if (node != null && node.Kind == SyntaxKind.VariableDeclarationList)
        {
            flags |= node.Flags;
            node = node.Parent!;
        }
        if (node != null && node.Kind == SyntaxKind.VariableStatement)
        {
            flags |= node.Flags;
        }
        return flags;
    }

    // utilities.go:1190-1192
    public static bool IsVarAwaitUsing(Node node)
    {
        return (GetCombinedNodeFlags(node) & NodeFlags.BlockScoped) == NodeFlags.AwaitUsing;
    }

    // utilities.go:1271-1273
    public static bool IsSourceFileJS(SourceFile file)
    {
        return file.ScriptKind == ScriptKind.JS || file.ScriptKind == ScriptKind.Jsx;
    }

    // utilities.go:1275-1277
    public static bool IsInJSFile(Node? node)
    {
        return node != null && (node.Flags & NodeFlags.JavaScriptFile) != 0;
    }
}

// utilities.go:772-788
[Flags]
public enum OuterExpressionKinds : ushort
{
    Parentheses = 1 << 0,
    TypeAssertions = 1 << 1,
    NonNullAssertions = 1 << 2,
    PartiallyEmittedExpressions = 1 << 3,
    ExpressionsWithTypeArguments = 1 << 4,
    Satisfies = 1 << 5,
    ExcludeJSDocTypeAssertion = 1 << 6,
    Assignments = 1 << 7,
    Comma = 1 << 8,
    Assertions = TypeAssertions | NonNullAssertions | Satisfies,
    All = Parentheses | Assertions | PartiallyEmittedExpressions | ExpressionsWithTypeArguments,
    AllExceptAssertionsOrExpressionsWithTypeArguments = All & ~Assertions & ~ExpressionsWithTypeArguments,
    ExpressionTypePassthrough = Parentheses | Assignments | Comma,
}

// utilities.go:932-938
public enum FindAncestorResult
{
    False, // FindAncestorFalse
    True, // FindAncestorTrue
    Quit, // FindAncestorQuit
}
