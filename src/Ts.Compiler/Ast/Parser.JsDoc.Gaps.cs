// ast-gap file for the parser JSDoc/reparser cluster (Parser.JsDoc.cs).
//
// These AST utilities are referenced by reparser.go's reparseHosted but are not yet ported into the
// ast package (grep-confirmed absent from Ts.Compiler/Ast at the time of writing). They are small and
// self-contained, so they are ported here verbatim from internal/ast/utilities.go rather than blocking
// the JSDoc reparse path. When the ast/utilities.go port lands, these should be removed in favour of
// the canonical AstUtilities members.
//
// NOTE: GetAssignmentDeclarationKind (utilities.go:1522) is intentionally NOT ported here — it pulls in
// a large unported dependency tree (IsModuleExportsAccessExpression / IsBindableObjectDefinePropertyCall
// / GetElementOrPropertyAccessName / …). Its two reparseHosted call sites (JS assignment-declaration
// @type/@satisfies) are deferred in Parser.JsDoc.cs with `// !!! ast-gap` markers.
namespace Ts.Ast;

internal static class JSDocReparseAstGaps
{
    // internal/ast/utilities.go:1344
    public static bool IsThisIdentifier(Node? node) =>
        node != null && NodeTests.IsIdentifier(node) && node.Text() == "this";

    // internal/ast/utilities.go:1623
    public static bool HasSamePropertyAccessName(Node node1, Node node2)
    {
        if (node1.Kind == SyntaxKind.Identifier && node2.Kind == SyntaxKind.Identifier)
        {
            return node1.Text() == node2.Text();
        }
        else if (node1.Kind == SyntaxKind.PropertyAccessExpression && node2.Kind == SyntaxKind.PropertyAccessExpression)
        {
            return node1.AsPropertyAccessExpression().Name()!.Text() == node2.AsPropertyAccessExpression().Name()!.Text() &&
                HasSamePropertyAccessName(node1.Expression()!, node2.Expression()!);
        }
        return false;
    }
}
