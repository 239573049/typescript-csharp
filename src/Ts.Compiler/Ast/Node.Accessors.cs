using System;
using System.Collections.Generic;
using Ts.Core;

namespace Ts.Ast;

// Go: internal/ast/ast.go — the hand-written, Kind-switch node accessors (ast.go:272-1151,
// deliberately switch-based rather than nodeData methods, ast.go:188-190) plus the
// convenience accessors from ast.go:192-270 and Contains (ast.go:1153-1167).
//
// PORT-NOTE (plan/02 §4.4 + phase-3 shadowing pre-warning): these are emitted as
// *extension methods* on Node, not instance methods. Every one shares its name with a
// generated node field/property (Text/Expression/Symbol/Body/Members/Statements/Type/…),
// so an instance method on Node would trip CS0108 (hide inherited member) against the
// generated *.g.cs classes, which cannot be hand-annotated with `new`. Extension methods
// keep the exact Go call form `node.Xxx()` (when the receiver is statically typed `Node`,
// which the port maintains everywhere) while avoiding the inheritance name-table clash.
//
// PORT-NOTE (nullability): Go `*Node` is inherently nilable and every generated node field
// is nullable-annotated, so the node/list/array accessors return `Node?`/`NodeList?`/
// `Node[]?`. Call sites add `!` where Go assumes non-nil (Go would panic on a nil deref;
// C# NRE is the faithful equivalent, plan/05 §4.3). This deviates from the simplified
// return-type column in plan/02 §4.4.
public static class NodeAccessors
{
    // ast.go:272-308
    public static string Text(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.Identifier:
                return n.AsIdentifier().Text;
            case SyntaxKind.PrivateIdentifier:
                return n.AsPrivateIdentifier().Text;
            case SyntaxKind.StringLiteral:
                return n.AsStringLiteral().Text;
            case SyntaxKind.NumericLiteral:
                return n.AsNumericLiteral().Text;
            case SyntaxKind.BigIntLiteral:
                return n.AsBigIntLiteral().Text;
            case SyntaxKind.MetaProperty:
                return n.AsMetaProperty().Name()!.Text();
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                return n.AsNoSubstitutionTemplateLiteral().Text;
            case SyntaxKind.TemplateHead:
                return n.AsTemplateHead().Text;
            case SyntaxKind.TemplateMiddle:
                return n.AsTemplateMiddle().Text;
            case SyntaxKind.TemplateTail:
                return n.AsTemplateTail().Text;
            case SyntaxKind.JsxNamespacedName:
                return n.AsJsxNamespacedName().Namespace!.Text() + ":" + n.AsJsxNamespacedName().name!.Text();
            case SyntaxKind.RegularExpressionLiteral:
                return n.AsRegularExpressionLiteral().Text;
            case SyntaxKind.JSDocText:
                return string.Concat(n.AsJSDocText().text ?? []);
            case SyntaxKind.JSDocLink:
                return string.Concat(n.AsJSDocLink().text ?? []);
            case SyntaxKind.JSDocLinkCode:
                return string.Concat(n.AsJSDocLinkCode().text ?? []);
            case SyntaxKind.JSDocLinkPlain:
                return string.Concat(n.AsJSDocLinkPlain().text ?? []);
        }
        throw new InvalidOperationException($"Unhandled case in Node.Text: {n.GetType().Name}");
    }

    // ast.go:310-384
    public static Node? Expression(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.PropertyAccessExpression:
                return n.AsPropertyAccessExpression().Expression;
            case SyntaxKind.ElementAccessExpression:
                return n.AsElementAccessExpression().Expression;
            case SyntaxKind.ParenthesizedExpression:
                return n.AsParenthesizedExpression().Expression;
            case SyntaxKind.CallExpression:
                return n.AsCallExpression().Expression;
            case SyntaxKind.NewExpression:
                return n.AsNewExpression().Expression;
            case SyntaxKind.ExpressionWithTypeArguments:
                return n.AsExpressionWithTypeArguments().Expression;
            case SyntaxKind.ComputedPropertyName:
                return n.AsComputedPropertyName().Expression;
            case SyntaxKind.NonNullExpression:
                return n.AsNonNullExpression().Expression;
            case SyntaxKind.TypeAssertionExpression:
                return n.AsTypeAssertion().Expression;
            case SyntaxKind.AsExpression:
                return n.AsAsExpression().Expression;
            case SyntaxKind.SatisfiesExpression:
                return n.AsSatisfiesExpression().Expression;
            case SyntaxKind.TypeOfExpression:
                return n.AsTypeOfExpression().Expression;
            case SyntaxKind.SpreadAssignment:
                return n.AsSpreadAssignment().Expression;
            case SyntaxKind.SpreadElement:
                return n.AsSpreadElement().Expression;
            case SyntaxKind.TemplateSpan:
                return n.AsTemplateSpan().Expression;
            case SyntaxKind.DeleteExpression:
                return n.AsDeleteExpression().Expression;
            case SyntaxKind.VoidExpression:
                return n.AsVoidExpression().Expression;
            case SyntaxKind.AwaitExpression:
                return n.AsAwaitExpression().Expression;
            case SyntaxKind.YieldExpression:
                return n.AsYieldExpression().Expression;
            case SyntaxKind.PartiallyEmittedExpression:
                return n.AsPartiallyEmittedExpression().Expression;
            case SyntaxKind.IfStatement:
                return n.AsIfStatement().Expression;
            case SyntaxKind.DoStatement:
                return n.AsDoStatement().Expression;
            case SyntaxKind.WhileStatement:
                return n.AsWhileStatement().Expression;
            case SyntaxKind.WithStatement:
                return n.AsWithStatement().Expression;
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
                return n.AsForInOrOfStatement().Expression;
            case SyntaxKind.SwitchStatement:
                return n.AsSwitchStatement().Expression;
            case SyntaxKind.CaseClause:
                return n.AsCaseOrDefaultClause().Expression;
            case SyntaxKind.ExpressionStatement:
                return n.AsExpressionStatement().Expression;
            case SyntaxKind.ReturnStatement:
                return n.AsReturnStatement().Expression;
            case SyntaxKind.ThrowStatement:
                return n.AsThrowStatement().Expression;
            case SyntaxKind.ExternalModuleReference:
                return n.AsExternalModuleReference().Expression;
            case SyntaxKind.ExportAssignment:
                return n.AsExportAssignment().Expression;
            case SyntaxKind.Decorator:
                return n.AsDecorator().Expression;
            case SyntaxKind.JsxExpression:
                return n.AsJsxExpression().Expression;
            case SyntaxKind.JsxSpreadAttribute:
                return n.AsJsxSpreadAttribute().Expression;
        }
        throw new InvalidOperationException("Unhandled case in Node.Expression: " + n.Kind);
    }

    // ast.go:386-396
    public static string RawText(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.TemplateHead:
                return n.AsTemplateHead().RawText;
            case SyntaxKind.TemplateMiddle:
                return n.AsTemplateMiddle().RawText;
            case SyntaxKind.TemplateTail:
                return n.AsTemplateTail().RawText;
        }
        throw new InvalidOperationException("Unhandled case in Node.RawText: " + n.Kind);
    }

    // ast.go:398-474 — MutableNode.SetExpression. Write permission expressed via
    // visibility (internal); AsMutable() is the identity form-preserver (ast.go:237).
    internal static void SetExpression(this Node n, Node? expr)
    {
        switch (n.Kind)
        {
            case SyntaxKind.PropertyAccessExpression:
                n.AsPropertyAccessExpression().Expression = expr;
                break;
            case SyntaxKind.ElementAccessExpression:
                n.AsElementAccessExpression().Expression = expr;
                break;
            case SyntaxKind.ParenthesizedExpression:
                n.AsParenthesizedExpression().Expression = expr;
                break;
            case SyntaxKind.CallExpression:
                n.AsCallExpression().Expression = expr;
                break;
            case SyntaxKind.NewExpression:
                n.AsNewExpression().Expression = expr;
                break;
            case SyntaxKind.ExpressionWithTypeArguments:
                n.AsExpressionWithTypeArguments().Expression = expr;
                break;
            case SyntaxKind.ComputedPropertyName:
                n.AsComputedPropertyName().Expression = expr;
                break;
            case SyntaxKind.NonNullExpression:
                n.AsNonNullExpression().Expression = expr;
                break;
            case SyntaxKind.TypeAssertionExpression:
                n.AsTypeAssertion().Expression = expr;
                break;
            case SyntaxKind.AsExpression:
                n.AsAsExpression().Expression = expr;
                break;
            case SyntaxKind.SatisfiesExpression:
                n.AsSatisfiesExpression().Expression = expr;
                break;
            case SyntaxKind.TypeOfExpression:
                n.AsTypeOfExpression().Expression = expr;
                break;
            case SyntaxKind.SpreadAssignment:
                n.AsSpreadAssignment().Expression = expr;
                break;
            case SyntaxKind.SpreadElement:
                n.AsSpreadElement().Expression = expr;
                break;
            case SyntaxKind.TemplateSpan:
                n.AsTemplateSpan().Expression = expr;
                break;
            case SyntaxKind.DeleteExpression:
                n.AsDeleteExpression().Expression = expr;
                break;
            case SyntaxKind.VoidExpression:
                n.AsVoidExpression().Expression = expr;
                break;
            case SyntaxKind.AwaitExpression:
                n.AsAwaitExpression().Expression = expr;
                break;
            case SyntaxKind.YieldExpression:
                n.AsYieldExpression().Expression = expr;
                break;
            case SyntaxKind.PartiallyEmittedExpression:
                n.AsPartiallyEmittedExpression().Expression = expr;
                break;
            case SyntaxKind.IfStatement:
                n.AsIfStatement().Expression = expr;
                break;
            case SyntaxKind.DoStatement:
                n.AsDoStatement().Expression = expr;
                break;
            case SyntaxKind.WhileStatement:
                n.AsWhileStatement().Expression = expr;
                break;
            case SyntaxKind.WithStatement:
                n.AsWithStatement().Expression = expr;
                break;
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
                n.AsForInOrOfStatement().Expression = expr;
                break;
            case SyntaxKind.SwitchStatement:
                n.AsSwitchStatement().Expression = expr;
                break;
            case SyntaxKind.CaseClause:
                n.AsCaseOrDefaultClause().Expression = expr;
                break;
            case SyntaxKind.ExpressionStatement:
                n.AsExpressionStatement().Expression = expr;
                break;
            case SyntaxKind.ReturnStatement:
                n.AsReturnStatement().Expression = expr;
                break;
            case SyntaxKind.ThrowStatement:
                n.AsThrowStatement().Expression = expr;
                break;
            case SyntaxKind.ExternalModuleReference:
                n.AsExternalModuleReference().Expression = expr;
                break;
            case SyntaxKind.ExportAssignment:
                n.AsExportAssignment().Expression = expr;
                break;
            case SyntaxKind.Decorator:
                n.AsDecorator().Expression = expr;
                break;
            case SyntaxKind.JsxExpression:
                n.AsJsxExpression().Expression = expr;
                break;
            case SyntaxKind.JsxSpreadAttribute:
                n.AsJsxSpreadAttribute().Expression = expr;
                break;
            default:
                throw new InvalidOperationException("Unhandled case in mutableNode.SetExpression: " + n.Kind);
        }
    }

    // ast.go:476-484
    public static NodeList? ArgumentList(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.CallExpression:
                return n.AsCallExpression().Arguments;
            case SyntaxKind.NewExpression:
                return n.AsNewExpression().Arguments;
        }
        throw new InvalidOperationException("Unhandled case in Node.Arguments: " + n.Kind);
    }

    // ast.go:486-492
    public static Node[]? Arguments(this Node n)
    {
        var list = n.ArgumentList();
        if (list != null)
        {
            return list.Nodes;
        }
        return null;
    }

    // ast.go:494-516
    public static NodeList? TypeArgumentList(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.CallExpression:
                return n.AsCallExpression().TypeArguments;
            case SyntaxKind.NewExpression:
                return n.AsNewExpression().TypeArguments;
            case SyntaxKind.TaggedTemplateExpression:
                return n.AsTaggedTemplateExpression().TypeArguments;
            case SyntaxKind.TypeReference:
                return n.AsTypeReferenceNode().TypeArguments;
            case SyntaxKind.ExpressionWithTypeArguments:
                return n.AsExpressionWithTypeArguments().TypeArguments;
            case SyntaxKind.ImportType:
                return n.AsImportTypeNode().TypeArguments;
            case SyntaxKind.TypeQuery:
                return n.AsTypeQueryNode().TypeArguments;
            case SyntaxKind.JsxOpeningElement:
                return n.AsJsxOpeningElement().TypeArguments;
            case SyntaxKind.JsxSelfClosingElement:
                return n.AsJsxSelfClosingElement().TypeArguments;
        }
        throw new InvalidOperationException("Unhandled case in Node.TypeArguments");
    }

    // ast.go:518-524
    public static Node[]? TypeArguments(this Node n)
    {
        var list = n.TypeArgumentList();
        if (list != null)
        {
            return list.Nodes;
        }
        return null;
    }

    // ast.go:526-545
    public static NodeList? TypeParameterList(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.ClassDeclaration:
                return n.AsClassDeclaration().TypeParameters;
            case SyntaxKind.ClassExpression:
                return n.AsClassExpression().TypeParameters;
            case SyntaxKind.InterfaceDeclaration:
                return n.AsInterfaceDeclaration().TypeParameters;
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                return n.AsTypeAliasDeclaration().TypeParameters;
            case SyntaxKind.JSDocTemplateTag:
                return n.AsJSDocTemplateTag().TypeParameters;
            default:
                var funcLike = n.FunctionLikeData();
                if (funcLike != null)
                {
                    return funcLike.TypeParameters;
                }
                break;
        }
        throw new InvalidOperationException("Unhandled case in Node.TypeParameterList");
    }

    // ast.go:547-553
    public static Node[]? TypeParameters(this Node n)
    {
        var list = n.TypeParameterList();
        if (list != null)
        {
            return list.Nodes;
        }
        return null;
    }

    // ast.go:555-571
    public static NodeList? MemberList(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.ClassDeclaration:
                return n.AsClassDeclaration().Members;
            case SyntaxKind.ClassExpression:
                return n.AsClassExpression().Members;
            case SyntaxKind.InterfaceDeclaration:
                return n.AsInterfaceDeclaration().Members;
            case SyntaxKind.EnumDeclaration:
                return n.AsEnumDeclaration().Members;
            case SyntaxKind.TypeLiteral:
                return n.AsTypeLiteralNode().Members;
            case SyntaxKind.MappedType:
                return n.AsMappedTypeNode().Members;
        }
        throw new InvalidOperationException("Unhandled case in Node.MemberList: " + n.Kind);
    }

    // ast.go:573-579
    public static Node[]? Members(this Node n)
    {
        var list = n.MemberList();
        if (list != null)
        {
            return list.Nodes;
        }
        return null;
    }

    // ast.go:581-593
    public static NodeList? StatementList(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.SourceFile:
                return n.AsSourceFile().Statements;
            case SyntaxKind.Block:
                return n.AsBlock().Statements;
            case SyntaxKind.ModuleBlock:
                return n.AsModuleBlock().Statements;
            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
                return n.AsCaseOrDefaultClause().Statements;
        }
        throw new InvalidOperationException("Unhandled case in Node.StatementList: " + n.Kind);
    }

    // ast.go:595-601
    public static Node[]? Statements(this Node n)
    {
        var list = n.StatementList();
        if (list != null)
        {
            return list.Nodes;
        }
        return null;
    }

    // ast.go:603-610
    public static bool CanHaveStatements(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.SourceFile:
            case SyntaxKind.Block:
            case SyntaxKind.ModuleBlock:
            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
                return true;
            default:
                return false;
        }
    }

    // ast.go:612-618
    public static ModifierFlags ModifierFlags(this Node n)
    {
        var modifiers = n.Modifiers();
        if (modifiers != null)
        {
            return modifiers.ModifierFlags;
        }
        return Ts.Ast.ModifierFlags.None;
    }

    // ast.go:620-626
    public static Node[]? ModifierNodes(this Node n)
    {
        var modifiers = n.Modifiers();
        if (modifiers != null)
        {
            return modifiers.Nodes;
        }
        return null;
    }

    // ast.go:628-686
    public static Node? Type(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.VariableDeclaration:
                return n.AsVariableDeclaration().Type;
            case SyntaxKind.Parameter:
                return n.AsParameterDeclaration().Type;
            case SyntaxKind.PropertySignature:
                return n.AsPropertySignatureDeclaration().Type;
            case SyntaxKind.PropertyDeclaration:
                return n.AsPropertyDeclaration().Type;
            case SyntaxKind.PropertyAssignment:
                return n.AsPropertyAssignment().Type;
            case SyntaxKind.ShorthandPropertyAssignment:
                return n.AsShorthandPropertyAssignment().Type;
            case SyntaxKind.TypePredicate:
                return n.AsTypePredicateNode().Type;
            case SyntaxKind.ParenthesizedType:
                return n.AsParenthesizedTypeNode().Type;
            case SyntaxKind.TypeOperator:
                return n.AsTypeOperatorNode().Type;
            case SyntaxKind.MappedType:
                return n.AsMappedTypeNode().Type;
            case SyntaxKind.TypeAssertionExpression:
                return n.AsTypeAssertion().Type;
            case SyntaxKind.AsExpression:
                return n.AsAsExpression().Type;
            case SyntaxKind.SatisfiesExpression:
                return n.AsSatisfiesExpression().Type;
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                return n.AsTypeAliasDeclaration().Type;
            case SyntaxKind.NamedTupleMember:
                return n.AsNamedTupleMember().Type;
            case SyntaxKind.OptionalType:
                return n.AsOptionalTypeNode().Type;
            case SyntaxKind.RestType:
                return n.AsRestTypeNode().Type;
            case SyntaxKind.TemplateLiteralTypeSpan:
                return n.AsTemplateLiteralTypeSpan().Type;
            case SyntaxKind.JSDocTypeExpression:
                return n.AsJSDocTypeExpression().Type;
            case SyntaxKind.JSDocParameterTag:
            case SyntaxKind.JSDocPropertyTag:
                return n.AsJSDocParameterOrPropertyTag().TypeExpression;
            case SyntaxKind.JSDocNullableType:
                return n.AsJSDocNullableType().Type;
            case SyntaxKind.JSDocNonNullableType:
                return n.AsJSDocNonNullableType().Type;
            case SyntaxKind.JSDocOptionalType:
                return n.AsJSDocOptionalType().Type;
            case SyntaxKind.ExportAssignment:
                return n.AsExportAssignment().Type;
            case SyntaxKind.BinaryExpression:
                return n.AsBinaryExpression().Type;
            default:
                var funcLike = n.FunctionLikeData();
                if (funcLike != null)
                {
                    return funcLike.Type;
                }
                break;
        }
        return null;
    }

    // ast.go:688-748 — MutableNode.SetType.
    internal static void SetType(this Node n, Node? t)
    {
        switch (n.Kind)
        {
            case SyntaxKind.VariableDeclaration:
                n.AsVariableDeclaration().Type = t;
                break;
            case SyntaxKind.Parameter:
                n.AsParameterDeclaration().Type = t;
                break;
            case SyntaxKind.PropertySignature:
                n.AsPropertySignatureDeclaration().Type = t;
                break;
            case SyntaxKind.PropertyDeclaration:
                n.AsPropertyDeclaration().Type = t;
                break;
            case SyntaxKind.PropertyAssignment:
                n.AsPropertyAssignment().Type = t;
                break;
            case SyntaxKind.ShorthandPropertyAssignment:
                n.AsShorthandPropertyAssignment().Type = t;
                break;
            case SyntaxKind.TypePredicate:
                n.AsTypePredicateNode().Type = t;
                break;
            case SyntaxKind.ParenthesizedType:
                n.AsParenthesizedTypeNode().Type = t;
                break;
            case SyntaxKind.TypeOperator:
                n.AsTypeOperatorNode().Type = t;
                break;
            case SyntaxKind.MappedType:
                n.AsMappedTypeNode().Type = t;
                break;
            case SyntaxKind.TypeAssertionExpression:
                n.AsTypeAssertion().Type = t;
                break;
            case SyntaxKind.AsExpression:
                n.AsAsExpression().Type = t;
                break;
            case SyntaxKind.SatisfiesExpression:
                n.AsSatisfiesExpression().Type = t;
                break;
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                n.AsTypeAliasDeclaration().Type = t;
                break;
            case SyntaxKind.NamedTupleMember:
                n.AsNamedTupleMember().Type = t;
                break;
            case SyntaxKind.OptionalType:
                n.AsOptionalTypeNode().Type = t;
                break;
            case SyntaxKind.RestType:
                n.AsRestTypeNode().Type = t;
                break;
            case SyntaxKind.TemplateLiteralTypeSpan:
                n.AsTemplateLiteralTypeSpan().Type = t;
                break;
            case SyntaxKind.JSDocTypeExpression:
                n.AsJSDocTypeExpression().Type = t;
                break;
            case SyntaxKind.JSDocParameterTag:
            case SyntaxKind.JSDocPropertyTag:
                n.AsJSDocParameterOrPropertyTag().TypeExpression = t;
                break;
            case SyntaxKind.JSDocNullableType:
                n.AsJSDocNullableType().Type = t;
                break;
            case SyntaxKind.JSDocNonNullableType:
                n.AsJSDocNonNullableType().Type = t;
                break;
            case SyntaxKind.JSDocOptionalType:
                n.AsJSDocOptionalType().Type = t;
                break;
            case SyntaxKind.ExportAssignment:
                n.AsExportAssignment().Type = t;
                break;
            case SyntaxKind.BinaryExpression:
                n.AsBinaryExpression().Type = t;
                break;
            default:
                var funcLike = n.FunctionLikeData();
                if (funcLike != null)
                {
                    funcLike.Type = t;
                }
                else
                {
                    throw new InvalidOperationException("Unhandled case in mutableNode.SetType: " + n.Kind);
                }
                break;
        }
    }

    // ast.go:750-773
    public static Node? Initializer(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.VariableDeclaration:
                return n.AsVariableDeclaration().Initializer;
            case SyntaxKind.Parameter:
                return n.AsParameterDeclaration().Initializer;
            case SyntaxKind.BindingElement:
                return n.AsBindingElement().Initializer;
            case SyntaxKind.PropertyDeclaration:
                return n.AsPropertyDeclaration().Initializer;
            case SyntaxKind.PropertySignature:
                return n.AsPropertySignatureDeclaration().Initializer;
            case SyntaxKind.PropertyAssignment:
                return n.AsPropertyAssignment().Initializer;
            case SyntaxKind.EnumMember:
                return n.AsEnumMember().Initializer;
            case SyntaxKind.ForStatement:
                return n.AsForStatement().Initializer;
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
                return n.AsForInOrOfStatement().Initializer;
            case SyntaxKind.JsxAttribute:
                return n.AsJsxAttribute().Initializer;
        }
        throw new InvalidOperationException("Unhandled case in Node.Initializer");
    }

    // ast.go:776-802 — MutableNode.SetInitializer.
    internal static void SetInitializer(this Node n, Node? initializer)
    {
        switch (n.Kind)
        {
            case SyntaxKind.VariableDeclaration:
                n.AsVariableDeclaration().Initializer = initializer;
                break;
            case SyntaxKind.Parameter:
                n.AsParameterDeclaration().Initializer = initializer;
                break;
            case SyntaxKind.BindingElement:
                n.AsBindingElement().Initializer = initializer;
                break;
            case SyntaxKind.PropertyDeclaration:
                n.AsPropertyDeclaration().Initializer = initializer;
                break;
            case SyntaxKind.PropertySignature:
                n.AsPropertySignatureDeclaration().Initializer = initializer;
                break;
            case SyntaxKind.PropertyAssignment:
                n.AsPropertyAssignment().Initializer = initializer;
                break;
            case SyntaxKind.EnumMember:
                n.AsEnumMember().Initializer = initializer;
                break;
            case SyntaxKind.ForStatement:
                n.AsForStatement().Initializer = initializer;
                break;
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
                n.AsForInOrOfStatement().Initializer = initializer;
                break;
            case SyntaxKind.JsxAttribute:
                n.AsJsxAttribute().Initializer = initializer;
                break;
            default:
                throw new InvalidOperationException("Unhandled case in mutableNode.SetInitializer");
        }
    }

    // ast.go:804-856
    public static Node? TagName(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.JsxOpeningElement:
                return n.AsJsxOpeningElement().TagName;
            case SyntaxKind.JsxClosingElement:
                return n.AsJsxClosingElement().TagName;
            case SyntaxKind.JsxSelfClosingElement:
                return n.AsJsxSelfClosingElement().TagName;
            case SyntaxKind.JSDocUnknownTag:
                return n.AsJSDocUnknownTag().TagName;
            case SyntaxKind.JSDocAugmentsTag:
                return n.AsJSDocAugmentsTag().TagName;
            case SyntaxKind.JSDocImplementsTag:
                return n.AsJSDocImplementsTag().TagName;
            case SyntaxKind.JSDocDeprecatedTag:
                return n.AsJSDocDeprecatedTag().TagName;
            case SyntaxKind.JSDocPublicTag:
                return n.AsJSDocPublicTag().TagName;
            case SyntaxKind.JSDocPrivateTag:
                return n.AsJSDocPrivateTag().TagName;
            case SyntaxKind.JSDocProtectedTag:
                return n.AsJSDocProtectedTag().TagName;
            case SyntaxKind.JSDocReadonlyTag:
                return n.AsJSDocReadonlyTag().TagName;
            case SyntaxKind.JSDocOverrideTag:
                return n.AsJSDocOverrideTag().TagName;
            case SyntaxKind.JSDocCallbackTag:
                return n.AsJSDocCallbackTag().TagName;
            case SyntaxKind.JSDocOverloadTag:
                return n.AsJSDocOverloadTag().TagName;
            case SyntaxKind.JSDocParameterTag:
            case SyntaxKind.JSDocPropertyTag:
                return n.AsJSDocParameterOrPropertyTag().TagName;
            case SyntaxKind.JSDocReturnTag:
                return n.AsJSDocReturnTag().TagName;
            case SyntaxKind.JSDocThisTag:
                return n.AsJSDocThisTag().TagName;
            case SyntaxKind.JSDocTypeTag:
                return n.AsJSDocTypeTag().TagName;
            case SyntaxKind.JSDocTemplateTag:
                return n.AsJSDocTemplateTag().TagName;
            case SyntaxKind.JSDocTypedefTag:
                return n.AsJSDocTypedefTag().TagName;
            case SyntaxKind.JSDocSeeTag:
                return n.AsJSDocSeeTag().TagName;
            case SyntaxKind.JSDocSatisfiesTag:
                return n.AsJSDocSatisfiesTag().TagName;
            case SyntaxKind.JSDocThrowsTag:
                return n.AsJSDocThrowsTag().TagName;
            case SyntaxKind.JSDocImportTag:
                return n.AsJSDocImportTag().TagName;
        }
        throw new InvalidOperationException("Unhandled case in Node.TagName: " + n.Kind);
    }

    // ast.go:858-868
    public static Node? PropertyName(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.ImportSpecifier:
                return n.AsImportSpecifier().PropertyName;
            case SyntaxKind.ExportSpecifier:
                return n.AsExportSpecifier().PropertyName;
            case SyntaxKind.BindingElement:
                return n.AsBindingElement().PropertyName;
        }
        return null;
    }

    // ast.go:870-876
    public static Node? PropertyNameOrName(this Node n)
    {
        var name = n.PropertyName();
        if (name == null)
        {
            name = n.Name();
        }
        return name;
    }

    // ast.go:878-892
    public static bool IsTypeOnly(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.ImportEqualsDeclaration:
                return n.AsImportEqualsDeclaration().IsTypeOnly;
            case SyntaxKind.ImportSpecifier:
                return n.AsImportSpecifier().IsTypeOnly;
            case SyntaxKind.ImportClause:
                return n.AsImportClause().PhaseModifier == SyntaxKind.TypeKeyword;
            case SyntaxKind.ExportDeclaration:
                return n.AsExportDeclaration().IsTypeOnly;
            case SyntaxKind.ExportSpecifier:
                return n.AsExportSpecifier().IsTypeOnly;
        }
        return false;
    }

    // ast.go:894-943 — If updating this function, also update `hasComment`.
    public static NodeList? CommentList(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.JSDoc:
                return n.AsJSDoc().Comment;
            case SyntaxKind.JSDocUnknownTag:
                return n.AsJSDocUnknownTag().Comment;
            case SyntaxKind.JSDocAugmentsTag:
                return n.AsJSDocAugmentsTag().Comment;
            case SyntaxKind.JSDocImplementsTag:
                return n.AsJSDocImplementsTag().Comment;
            case SyntaxKind.JSDocDeprecatedTag:
                return n.AsJSDocDeprecatedTag().Comment;
            case SyntaxKind.JSDocPublicTag:
                return n.AsJSDocPublicTag().Comment;
            case SyntaxKind.JSDocPrivateTag:
                return n.AsJSDocPrivateTag().Comment;
            case SyntaxKind.JSDocProtectedTag:
                return n.AsJSDocProtectedTag().Comment;
            case SyntaxKind.JSDocReadonlyTag:
                return n.AsJSDocReadonlyTag().Comment;
            case SyntaxKind.JSDocOverrideTag:
                return n.AsJSDocOverrideTag().Comment;
            case SyntaxKind.JSDocCallbackTag:
                return n.AsJSDocCallbackTag().Comment;
            case SyntaxKind.JSDocOverloadTag:
                return n.AsJSDocOverloadTag().Comment;
            case SyntaxKind.JSDocParameterTag:
            case SyntaxKind.JSDocPropertyTag:
                return n.AsJSDocParameterOrPropertyTag().Comment;
            case SyntaxKind.JSDocReturnTag:
                return n.AsJSDocReturnTag().Comment;
            case SyntaxKind.JSDocThisTag:
                return n.AsJSDocThisTag().Comment;
            case SyntaxKind.JSDocTypeTag:
                return n.AsJSDocTypeTag().Comment;
            case SyntaxKind.JSDocTemplateTag:
                return n.AsJSDocTemplateTag().Comment;
            case SyntaxKind.JSDocTypedefTag:
                return n.AsJSDocTypedefTag().Comment;
            case SyntaxKind.JSDocSeeTag:
                return n.AsJSDocSeeTag().Comment;
            case SyntaxKind.JSDocSatisfiesTag:
                return n.AsJSDocSatisfiesTag().Comment;
            case SyntaxKind.JSDocThrowsTag:
                return n.AsJSDocThrowsTag().Comment;
            case SyntaxKind.JSDocImportTag:
                return n.AsJSDocImportTag().Comment;
        }
        throw new InvalidOperationException("Unhandled case in Node.CommentList: " + n.Kind);
    }

    // ast.go:945-951
    public static Node[]? Comments(this Node n)
    {
        var list = n.CommentList();
        if (list != null)
        {
            return list.Nodes;
        }
        return null;
    }

    // ast.go:953-963
    public static Node? Label(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.LabeledStatement:
                return n.AsLabeledStatement().Label;
            case SyntaxKind.BreakStatement:
                return n.AsBreakStatement().Label;
            case SyntaxKind.ContinueStatement:
                return n.AsContinueStatement().Label;
        }
        throw new InvalidOperationException("Unhandled case in Node.Label: " + n.Kind);
    }

    // ast.go:965-973
    public static Node? Attributes(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.JsxOpeningElement:
                return n.AsJsxOpeningElement().Attributes;
            case SyntaxKind.JsxSelfClosingElement:
                return n.AsJsxSelfClosingElement().Attributes;
        }
        throw new InvalidOperationException("Unhandled case in Node.Attributes: " + n.Kind);
    }

    // ast.go:975-983
    public static NodeList? Children(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.JsxElement:
                return n.AsJsxElement().Children;
            case SyntaxKind.JsxFragment:
                return n.AsJsxFragment().Children;
        }
        throw new InvalidOperationException("Unhandled case in Node.Children: " + n.Kind);
    }

    // ast.go:985-995
    public static Node? ModuleSpecifier(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
                return n.AsImportDeclaration().ModuleSpecifier;
            case SyntaxKind.ExportDeclaration:
                return n.AsExportDeclaration().ModuleSpecifier;
            case SyntaxKind.JSDocImportTag:
                return n.AsJSDocImportTag().ModuleSpecifier;
        }
        throw new InvalidOperationException("Unhandled case in Node.ModuleSpecifier: " + n.Kind);
    }

    // ast.go:997-1005
    public static Node? ImportClause(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
                return n.AsImportDeclaration().ImportClause;
            case SyntaxKind.JSDocImportTag:
                return n.AsJSDocImportTag().ImportClause;
        }
        throw new InvalidOperationException("Unhandled case in Node.ImportClause: " + n.Kind);
    }

    // ast.go:1007-1023
    public static Node? Statement(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.DoStatement:
                return n.AsDoStatement().Statement;
            case SyntaxKind.WhileStatement:
                return n.AsWhileStatement().Statement;
            case SyntaxKind.ForStatement:
                return n.AsForStatement().Statement;
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
                return n.AsForInOrOfStatement().Statement;
            case SyntaxKind.WithStatement:
                return n.AsWithStatement().Statement;
            case SyntaxKind.LabeledStatement:
                return n.AsLabeledStatement().Statement;
        }
        throw new InvalidOperationException("Unhandled case in Node.Statement: " + n.Kind);
    }

    // ast.go:1025-1033
    public static NodeList? PropertyList(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.ObjectLiteralExpression:
                return n.AsObjectLiteralExpression().Properties;
            case SyntaxKind.JsxAttributes:
                return n.AsJsxAttributes().Properties;
        }
        throw new InvalidOperationException("Unhandled case in Node.PropertyList: " + n.Kind);
    }

    // ast.go:1035-1041
    public static Node[]? Properties(this Node n)
    {
        var list = n.PropertyList();
        if (list != null)
        {
            return list.Nodes;
        }
        return null;
    }

    // ast.go:1043-1057
    public static NodeList? ElementList(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.NamedImports:
                return n.AsNamedImports().Elements;
            case SyntaxKind.NamedExports:
                return n.AsNamedExports().Elements;
            case SyntaxKind.ObjectBindingPattern:
            case SyntaxKind.ArrayBindingPattern:
                return n.AsBindingPattern().Elements;
            case SyntaxKind.ArrayLiteralExpression:
                return n.AsArrayLiteralExpression().Elements;
            case SyntaxKind.TupleType:
                return n.AsTupleTypeNode().Elements;
        }
        throw new InvalidOperationException("Unhandled case in Node.ElementList: " + n.Kind);
    }

    // ast.go:1059-1065
    public static Node[]? Elements(this Node n)
    {
        var list = n.ElementList();
        if (list != null)
        {
            return list.Nodes;
        }
        return null;
    }

    // ast.go:1067-1089
    public static Node? PostfixToken(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.MethodDeclaration:
                return n.AsMethodDeclaration().PostfixToken;
            case SyntaxKind.ShorthandPropertyAssignment:
                return n.AsShorthandPropertyAssignment().PostfixToken;
            case SyntaxKind.MethodSignature:
                return n.AsMethodSignatureDeclaration().PostfixToken;
            case SyntaxKind.PropertySignature:
                return n.AsPropertySignatureDeclaration().PostfixToken;
            case SyntaxKind.PropertyAssignment:
                return n.AsPropertyAssignment().PostfixToken;
            case SyntaxKind.PropertyDeclaration:
                return n.AsPropertyDeclaration().PostfixToken;
            case SyntaxKind.EnumMember:
                return n.AsEnumMember().PostfixToken;
            case SyntaxKind.GetAccessor:
                return n.AsGetAccessorDeclaration().PostfixToken;
            case SyntaxKind.SetAccessor:
                return n.AsSetAccessorDeclaration().PostfixToken;
        }
        return null;
    }

    // ast.go:1091-1107
    public static Node? QuestionToken(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.Parameter:
                return n.AsParameterDeclaration().QuestionToken;
            case SyntaxKind.ConditionalExpression:
                return n.AsConditionalExpression().QuestionToken;
            case SyntaxKind.MappedType:
                return n.AsMappedTypeNode().QuestionToken;
            case SyntaxKind.NamedTupleMember:
                return n.AsNamedTupleMember().QuestionToken;
        }
        var postfix = n.PostfixToken();
        if (postfix != null && postfix.Kind == SyntaxKind.QuestionToken)
        {
            return postfix;
        }
        return null;
    }

    // ast.go:1109-1121
    public static Node? QuestionDotToken(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.ElementAccessExpression:
                return n.AsElementAccessExpression().QuestionDotToken;
            case SyntaxKind.PropertyAccessExpression:
                return n.AsPropertyAccessExpression().QuestionDotToken;
            case SyntaxKind.CallExpression:
                return n.AsCallExpression().QuestionDotToken;
            case SyntaxKind.TaggedTemplateExpression:
                return n.AsTaggedTemplateExpression().QuestionDotToken;
        }
        throw new InvalidOperationException("Unhandled case in Node.QuestionDotToken: " + n.Kind);
    }

    // ast.go:1123-1141
    public static Node? TypeExpression(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.JSDocParameterTag:
            case SyntaxKind.JSDocPropertyTag:
                return n.AsJSDocParameterOrPropertyTag().TypeExpression;
            case SyntaxKind.JSDocReturnTag:
                return n.AsJSDocReturnTag().TypeExpression;
            case SyntaxKind.JSDocTypeTag:
                return n.AsJSDocTypeTag().TypeExpression;
            case SyntaxKind.JSDocTypedefTag:
                return n.AsJSDocTypedefTag().TypeExpression;
            case SyntaxKind.JSDocCallbackTag:
                return n.AsJSDocCallbackTag().TypeExpression;
            case SyntaxKind.JSDocSatisfiesTag:
                return n.AsJSDocSatisfiesTag().TypeExpression;
            case SyntaxKind.JSDocThrowsTag:
                return n.AsJSDocThrowsTag().TypeExpression;
        }
        throw new InvalidOperationException("Unhandled case in Node.TypeExpression: " + n.Kind);
    }

    // ast.go:1143-1151
    public static Node? ClassName(this Node n)
    {
        switch (n.Kind)
        {
            case SyntaxKind.JSDocAugmentsTag:
                return n.AsJSDocAugmentsTag().ClassName;
            case SyntaxKind.JSDocImplementsTag:
                return n.AsJSDocImplementsTag().ClassName;
        }
        throw new InvalidOperationException("Unhandled case in Node.ClassName: " + n.Kind);
    }

    // ── Convenience accessors (ast.go:192-270). Extension methods for the same
    //    shadowing reason as above (Symbol/LocalSymbol/Locals/Body/Parameters collide
    //    with generated mixin properties). ──

    // ast.go:195-206 — Go iter.Seq; buffered List per plan/02 §4.1 (loses early-stop
    // laziness; ForEachChild's `true` short-circuit is not exposed here).
    public static IEnumerable<Node> IterChildren(this Node n)
    {
        var result = new List<Node>();
        n.ForEachChild(child =>
        {
            result.Add(child);
            return false;
        });
        return result;
    }

    // ast.go:216-217
    public static NodeList? ParameterList(this Node n)
    {
        return n.FunctionLikeData()!.Parameters;
    }

    public static Node[] Parameters(this Node n)
    {
        return n.ParameterList()!.Nodes;
    }

    // ast.go:228-233
    public static Node[]? Decorators(this Node n)
    {
        if (n.Modifiers() == null)
        {
            return null;
        }
        return CoreUtil.Filter(n.Modifiers()!.Nodes, NodeTests.IsDecorator);
    }

    // ast.go:237 — MutableNode identity (R9: keep the one-liner to preserve call form).
    public static Node AsMutable(this Node n)
    {
        return n;
    }

    // ast.go:240-246
    public static Symbol? Symbol(this Node n)
    {
        var data = n.DeclarationData();
        if (data != null)
        {
            return data.Symbol;
        }
        return null;
    }

    // ast.go:248-254
    public static Symbol? LocalSymbol(this Node n)
    {
        var data = n.ExportableData();
        if (data != null)
        {
            return data.LocalSymbol;
        }
        return null;
    }

    // ast.go:256-262
    public static SymbolTable? Locals(this Node n)
    {
        var data = n.LocalsContainerData();
        if (data != null)
        {
            return data.Locals;
        }
        return null;
    }

    // ast.go:264-270
    public static Node? Body(this Node n)
    {
        var data = n.BodyData();
        if (data != null)
        {
            return data.Body;
        }
        return null;
    }

    // ast.go:1153-1167 — Determines if `n` contains `descendant` by walking up the
    // Parent pointers. Panics if `descendant` or an ancestor is unparented (except a
    // SourceFile).
    public static bool Contains(this Node n, Node? descendant)
    {
        while (descendant != null)
        {
            if (descendant == n)
            {
                return true;
            }
            var parent = descendant.Parent;
            if (parent == null && !NodeTests.IsSourceFile(descendant))
            {
                throw new InvalidOperationException("descendant is not parented");
            }
            descendant = parent;
        }
        return false;
    }

    // ast.go:1171-1177 — Node casts for the flow-graph borrow types. The synthetic
    // FlowSwitchClauseData/FlowReduceLabelData land with the binder port (phase 4);
    // AsFlowSwitchClauseData/AsFlowReduceLabelData are deferred until those types exist.
}
