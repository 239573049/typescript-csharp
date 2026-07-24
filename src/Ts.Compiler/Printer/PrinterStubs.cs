// Stubs for references from the printer package (factory.go/helpers.go wave) into functionality that has
// not yet been ported. Each stub throws NotImplementedException naming the Go symbol it stands in for, so a
// hit at runtime is obvious and the remaining work is discoverable via grep.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Printer;

// PORT-NOTE: Stubs for the remaining internal/printer/utilities.go functions that printer.go references but
// that are not part of this emit-body wave (utilities.go is a later wave). Each throws NotImplementedException
// naming the Go symbol. These are only reached on the comment-recognition and `preserveSourceNewlines`
// emit paths (isJSDocLikeText / IsPinnedComment / IsRecognizedTripleSlashComment / calculateIndent /
// siblingNodePositionsAreComparable / originalNodesHaveSameParent) and on unique-name generation with a
// currentSourceFile (IsFileLevelUniqueName). Port them with the rest of utilities.go and remove these.
public static partial class PrinterUtilities
{
    // utilities.go:422 — siblingNodePositionsAreComparable.
    internal static bool SiblingNodePositionsAreComparable(EmitContext emitContext, Node previousNode, Node nextNode)
    {
        if (nextNode.Pos < previousNode.End)
        {
            return false;
        }

        previousNode = emitContext.MostOriginal(previousNode)!;
        nextNode = emitContext.MostOriginal(nextNode)!;
        Node? parent = previousNode.Parent;
        if (parent == null || parent != nextNode.Parent)
        {
            return false;
        }

        NodeList? parentNodeArray = GetContainingNodeArray(previousNode);
        if (parentNodeArray != null)
        {
            int prevNodeIndex = Array.IndexOf(parentNodeArray.Nodes, previousNode);
            return prevNodeIndex >= 0 && Array.IndexOf(parentNodeArray.Nodes, nextNode) == prevNodeIndex + 1;
        }

        return false;
    }

    // utilities.go:443 — getContainingNodeArray.
    private static NodeList? GetContainingNodeArray(Node node)
    {
        Node? parent = node.Parent;
        if (parent == null)
        {
            return null;
        }

        switch (node.Kind)
        {
            case SyntaxKind.TypeParameter:
                if (AstUtilities.IsFunctionLike(parent) || AstUtilities.IsClassLike(parent) || NodeTests.IsInterfaceDeclaration(parent) || AstUtilities.IsTypeOrJSTypeAliasDeclaration(parent))
                {
                    return parent.TypeParameterList();
                }
                else if (NodeTests.IsInferTypeNode(parent))
                {
                    // Falls through to the parent-kind switch below.
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected TypeParameter parent: {parent.Kind}");
                }
                break;
            case SyntaxKind.Parameter:
                return node.Parent!.ParameterList();
            case SyntaxKind.TemplateLiteralTypeSpan:
                return node.Parent!.AsTemplateLiteralTypeNode().TemplateSpans;
            case SyntaxKind.TemplateSpan:
                return node.Parent!.AsTemplateExpression().TemplateSpans;
            case SyntaxKind.Decorator:
                if (AstUtilities.CanHaveDecorators(node.Parent!))
                {
                    ModifierList? modifiers = node.Parent!.Modifiers();
                    if (modifiers != null)
                    {
                        return modifiers;
                    }
                }
                return null;
            case SyntaxKind.HeritageClause:
                if (AstUtilities.IsClassLike(node.Parent!))
                {
                    return node.Parent!.ClassLikeData()!.HeritageClauses;
                }
                return node.Parent!.AsInterfaceDeclaration().HeritageClauses;
        }

        switch (parent.Kind)
        {
            case SyntaxKind.TypeLiteral:
            case SyntaxKind.InterfaceDeclaration:
                if (AstUtilities.IsTypeElement(node))
                {
                    return parent.MemberList();
                }
                break;
            case SyntaxKind.UnionType:
                return parent.AsUnionTypeNode().Types;
            case SyntaxKind.IntersectionType:
                return parent.AsIntersectionTypeNode().Types;
            case SyntaxKind.ArrayLiteralExpression:
            case SyntaxKind.TupleType:
            case SyntaxKind.NamedImports:
            case SyntaxKind.NamedExports:
                return parent.ElementList();
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.JsxAttributes:
                return parent.PropertyList();
            case SyntaxKind.CallExpression:
            {
                CallExpression p = parent.AsCallExpression();
                if (AstUtilities.IsTypeNode(node))
                {
                    return p.TypeArguments;
                }
                if (node != p.Expression)
                {
                    return p.Arguments;
                }
                break;
            }
            case SyntaxKind.NewExpression:
            {
                NewExpression p = parent.AsNewExpression();
                if (AstUtilities.IsTypeNode(node))
                {
                    return p.TypeArguments;
                }
                if (node != p.Expression)
                {
                    return p.Arguments;
                }
                break;
            }
            case SyntaxKind.JsxElement:
            case SyntaxKind.JsxFragment:
                if (AstUtilities.IsJsxChild(node))
                {
                    return parent.Children();
                }
                break;
            case SyntaxKind.JsxOpeningElement:
            case SyntaxKind.JsxSelfClosingElement:
                if (AstUtilities.IsTypeNode(node))
                {
                    return parent.TypeArgumentList();
                }
                break;
            case SyntaxKind.Block:
            case SyntaxKind.ModuleBlock:
            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
                return parent.StatementList();
            case SyntaxKind.CaseBlock:
                return parent.AsCaseBlock().Clauses;
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
                if (AstUtilities.IsClassElement(node))
                {
                    return parent.MemberList();
                }
                break;
            case SyntaxKind.EnumDeclaration:
                if (NodeTests.IsEnumMember(node))
                {
                    return parent.MemberList();
                }
                break;
            case SyntaxKind.SourceFile:
                if (AstUtilities.IsStatement(node))
                {
                    return parent.StatementList();
                }
                break;
        }

        if (AstUtilities.IsModifier(node))
        {
            ModifierList? modifiers = parent.Modifiers();
            if (modifiers != null)
            {
                return modifiers;
            }
        }

        return null;
    }
}

internal static class PrinterStubs
{
    // ast.TryGetPropertyNameOfBindingOrAssignmentElement (internal/ast/utilities.go:3900), called by
    // NodeFactory.NewRestHelper — now landed in Ts.Ast.AstUtilities.
    internal static Node? TryGetPropertyNameOfBindingOrAssignmentElement(Node bindingElement) =>
        global::Ts.Ast.AstUtilities.TryGetPropertyNameOfBindingOrAssignmentElement(bindingElement);
}
