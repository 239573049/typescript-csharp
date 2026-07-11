// Ported from tsgo internal/format/indent.go (partial)
//
// PORT-NOTE (breadth-first, per dispatch): indent.go (821 lines) is the smart
// indenter. The two rule-facing decision functions used by the rule engine and by
// span.go's delta computation — ShouldIndentChildNode / NodeWillIndentChild — are
// ported verbatim here because they are self-contained and load-bearing for
// getOwnOrInheritedDelta. The deep indentation-inference entry point
// GetIndentationForNode (SmartIndenter machinery: getIndentationForNodeWorker,
// dynamicIndenter, getActualIndentation…) is left as a documented stub returning 0
// until the full indenter is ported; this affects only the *amount* of indentation
// inserted, not which whitespace/token rules fire.

using Ts.Ast;
using Ts.Core;

namespace Ts.Format;

internal static class Indent
{
    // Go: func GetIndentationForNode(n, ignoreActualIndentationRange, sourceFile, options) int
    //
    // !!! STUB: full SmartIndenter port pending. The real implementation walks the
    // node's ancestors computing the indentation column. Returning 0 yields the base
    // (column-0) indentation. The rule engine (spacing/token rules) is unaffected;
    // only computed indent columns differ from tsgo until this is completed.
    internal static int GetIndentationForNode(Node n, TextRange ignoreActualIndentationRange, SourceFile sourceFile, FormatCodeSettings options)
    {
        // !!! TODO: port SmartIndenter.getIndentationForNode
        return 0;
    }

    // Go: func ShouldIndentChildNode(settings, parent, child, sourceFile, isNextChildArg ...bool) bool
    internal static bool ShouldIndentChildNode(FormatCodeSettings settings, Node parent, Node? child, SourceFile? sourceFile, bool isNextChild = false)
    {
        return NodeWillIndentChild(settings, parent, child, sourceFile, false) &&
               !(isNextChild && child is not null && IsControlFlowEndingStatement(child.Kind, parent.Kind));
    }

    // Go: func NodeWillIndentChild(settings, parent, child, sourceFile, indentByDefault) bool
    internal static bool NodeWillIndentChild(FormatCodeSettings settings, Node parent, Node? child, SourceFile? sourceFile, bool indentByDefault)
    {
        SyntaxKind childKind = SyntaxKind.Unknown;
        if (child is not null)
        {
            childKind = child.Kind;
        }

        switch (parent.Kind)
        {
            case SyntaxKind.ExpressionStatement:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.ArrayLiteralExpression:
            case SyntaxKind.Block:
            case SyntaxKind.ModuleBlock:
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.TypeLiteral:
            case SyntaxKind.MappedType:
            case SyntaxKind.TupleType:
            case SyntaxKind.ParenthesizedExpression:
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.CallExpression:
            case SyntaxKind.NewExpression:
            case SyntaxKind.VariableStatement:
            case SyntaxKind.ExportAssignment:
            case SyntaxKind.ReturnStatement:
            case SyntaxKind.ConditionalExpression:
            case SyntaxKind.ArrayBindingPattern:
            case SyntaxKind.ObjectBindingPattern:
            case SyntaxKind.JsxOpeningElement:
            case SyntaxKind.JsxOpeningFragment:
            case SyntaxKind.JsxSelfClosingElement:
            case SyntaxKind.JsxExpression:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.CallSignature:
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.Parameter:
            case SyntaxKind.FunctionType:
            case SyntaxKind.ConstructorType:
            case SyntaxKind.ParenthesizedType:
            case SyntaxKind.TaggedTemplateExpression:
            case SyntaxKind.AwaitExpression:
            case SyntaxKind.NamedExports:
            case SyntaxKind.NamedImports:
            case SyntaxKind.ExportSpecifier:
            case SyntaxKind.ImportSpecifier:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
                return true;
            case SyntaxKind.CaseBlock:
                return settings.IndentSwitchCase.IsTrueOrUnknown();
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.BinaryExpression:
                if (settings.IndentMultiLineObjectLiteralBeginningOnBlankLine.IsFalseOrUnknown() && sourceFile is not null && childKind == SyntaxKind.ObjectLiteralExpression)
                {
                    return Util.RangeIsOnOneLine(child!.Loc, sourceFile);
                }
                if (parent.Kind == SyntaxKind.BinaryExpression && sourceFile is not null && childKind == SyntaxKind.JsxElement)
                {
                    int parentStartLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), parent.Pos));
                    int childStartLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), child!.Pos));
                    return parentStartLine != childStartLine;
                }
                if (parent.Kind != SyntaxKind.BinaryExpression)
                {
                    return true;
                }
                return indentByDefault;
            case SyntaxKind.DoStatement:
            case SyntaxKind.WhileStatement:
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
            case SyntaxKind.ForStatement:
            case SyntaxKind.IfStatement:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.Constructor:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                return childKind != SyntaxKind.Block;
            case SyntaxKind.ArrowFunction:
                if (sourceFile is not null && childKind == SyntaxKind.ParenthesizedExpression)
                {
                    return Util.RangeIsOnOneLine(child!.Loc, sourceFile);
                }
                return childKind != SyntaxKind.Block;
            case SyntaxKind.ExportDeclaration:
                return childKind != SyntaxKind.NamedExports;
            case SyntaxKind.ImportDeclaration:
                return childKind != SyntaxKind.ImportClause || (child!.AsImportClause().NamedBindings is not null && child.AsImportClause().NamedBindings!.Kind != SyntaxKind.NamedImports);
            case SyntaxKind.JsxElement:
                return childKind != SyntaxKind.JsxClosingElement;
            case SyntaxKind.JsxFragment:
                return childKind != SyntaxKind.JsxClosingFragment;
            case SyntaxKind.IntersectionType:
            case SyntaxKind.UnionType:
            case SyntaxKind.SatisfiesExpression:
                if (childKind == SyntaxKind.TypeLiteral || childKind == SyntaxKind.TupleType || childKind == SyntaxKind.MappedType)
                {
                    return false;
                }
                return indentByDefault;
            case SyntaxKind.TryStatement:
                if (childKind == SyntaxKind.Block)
                {
                    return false;
                }
                return indentByDefault;
        }

        // No explicit rule for given nodes so the result will follow the default value argument
        return indentByDefault;
    }

    // Go: func isControlFlowEndingStatement(kind ast.Kind, parentKind ast.Kind) bool
    internal static bool IsControlFlowEndingStatement(SyntaxKind kind, SyntaxKind parentKind)
    {
        switch (kind)
        {
            case SyntaxKind.ReturnStatement:
            case SyntaxKind.ThrowStatement:
            case SyntaxKind.ContinueStatement:
            case SyntaxKind.BreakStatement:
                return parentKind != SyntaxKind.Block;
            default:
                return false;
        }
    }
}
