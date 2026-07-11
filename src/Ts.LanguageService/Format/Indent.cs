// Ported from tsgo internal/format/indent.go

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Format;

internal static class Indent
{
    internal static int GetIndentationForNode(Node n, TextRange ignoreActualIndentationRange, SourceFile sourceFile, FormatCodeSettings options)
    {
        (int startLine, int startPosition) = global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(
            sourceFile,
            global::Ts.Scanner.Scanner.GetTokenPosOfNode(n, sourceFile, false));
        return GetIndentationForNodeWorker(n, startLine, startPosition, ignoreActualIndentationRange, 0, sourceFile, false, options);
    }

    private static int GetIndentationForNodeWorker(
        Node current,
        int currentStartLine,
        int currentStartCharacter,
        TextRange? ignoreActualIndentationRange,
        int indentationDelta,
        SourceFile sourceFile,
        bool isNextChild,
        FormatCodeSettings options)
    {
        Node? parent = current.Parent;

        // Walk up the tree and collect indentation for parent-child node pairs. Indentation is not added if
        // * parent and child nodes start on the same line, or
        // * parent is an IfStatement and child starts on the same line as an 'else clause'.
        while (parent is not null)
        {
            bool useActualIndentation = true;
            if (ignoreActualIndentationRange is TextRange ignoredRange)
            {
                int start = global::Ts.Scanner.Scanner.GetTokenPosOfNode(current, sourceFile, false);
                useActualIndentation = start < ignoredRange.Pos || start > ignoredRange.End;
            }

            (int containingListOrParentStartLine, int containingListOrParentStartCharacter) =
                GetContainingListOrParentStart(parent, current, sourceFile);
            bool parentAndChildShareLine = containingListOrParentStartLine == currentStartLine ||
                ChildStartsOnTheSameLineWithElseInIfStatement(parent, current, currentStartLine, sourceFile);

            if (useActualIndentation)
            {
                // check if current node is a list item - if yes, take indentation from it
                Node? firstListChild = null;
                NodeList? containerList = GetContainingList(current, sourceFile);
                if (containerList is not null && containerList.Nodes.Length > 0)
                {
                    firstListChild = containerList.Nodes[0];
                }

                // A list indents its children if the children begin on a later line than the list itself.
                bool listIndentsChild = false;
                if (firstListChild is not null)
                {
                    int listLine = GetStartLineForNode(firstListChild, sourceFile);
                    listIndentsChild = listLine > containingListOrParentStartLine;
                }

                int actualIndentation = GetActualIndentationForListItem(current, sourceFile, options, listIndentsChild);
                if (actualIndentation != -1)
                {
                    return actualIndentation + indentationDelta;
                }

                // try to fetch actual indentation for current node from source text
                actualIndentation = GetActualIndentationForNode(
                    current,
                    parent,
                    currentStartLine,
                    currentStartCharacter,
                    parentAndChildShareLine,
                    sourceFile,
                    options);
                if (actualIndentation != -1)
                {
                    return actualIndentation + indentationDelta;
                }
            }

            // increase indentation if parent node wants its content to be indented and parent and child nodes don't start on the same line
            if (ShouldIndentChildNode(options, parent, current, sourceFile, isNextChild) && !parentAndChildShareLine)
            {
                indentationDelta += options.IndentSize;
            }

            // In our AST, a call argument's parent is the call-expression, not the argument list.
            bool useTrueStart = IsArgumentAndStartLineOverlapsExpressionBeingCalled(parent, current, currentStartLine, sourceFile);

            current = parent;
            parent = current.Parent;

            if (useTrueStart)
            {
                (currentStartLine, currentStartCharacter) = global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(
                    sourceFile,
                    global::Ts.Scanner.Scanner.GetTokenPosOfNode(current, sourceFile, false));
            }
            else
            {
                currentStartLine = containingListOrParentStartLine;
                currentStartCharacter = containingListOrParentStartCharacter;
            }
        }

        return indentationDelta + options.BaseIndentSize;
    }

    /*
    * Function returns -1 if actual indentation for node should not be used (i.e because node is nested expression)
    */
    private static int GetActualIndentationForNode(
        Node current,
        Node parent,
        int currentLine,
        int currentChar,
        bool parentAndChildShareLine,
        SourceFile sourceFile,
        FormatCodeSettings options)
    {
        // actual indentation is used for statements\declarations if one of cases below is true:
        // - parent is SourceFile - by default immediate children of SourceFile are not indented except when user indents them manually
        // - parent and child are not on the same line
        bool useActualIndentation = (global::Ts.Ast.AstUtilities.IsDeclaration(current) || global::Ts.Ast.AstUtilities.IsStatementButNotDeclaration(current)) &&
            (parent.Kind == SyntaxKind.SourceFile || !parentAndChildShareLine);

        if (!useActualIndentation)
        {
            return -1;
        }

        return FindColumnForFirstNonWhitespaceCharacterInLine(currentLine, currentChar, sourceFile, options);
    }

    private static bool IsArgumentAndStartLineOverlapsExpressionBeingCalled(Node parent, Node child, int childStartLine, SourceFile sourceFile)
    {
        Node[]? arguments = parent.Kind == SyntaxKind.CallExpression ? parent.Arguments() : null;
        if (arguments is null || Array.IndexOf(arguments, child) < 0)
        {
            return false;
        }
        int expressionOfCallExpressionEnd = parent.Expression()!.End;
        int expressionOfCallExpressionEndLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, expressionOfCallExpressionEnd);
        return expressionOfCallExpressionEndLine == childStartLine;
    }

    private static int GetActualIndentationForListItem(Node node, SourceFile sourceFile, FormatCodeSettings options, bool listIndentsChild)
    {
        if (node.Parent is not null && node.Parent.Kind == SyntaxKind.VariableDeclarationList)
        {
            // VariableDeclarationList has no wrapping tokens
            return -1;
        }
        NodeList? containingList = GetContainingList(node, sourceFile);
        if (containingList is not null)
        {
            int index = Array.IndexOf(containingList.Nodes, node);
            if (index != -1)
            {
                int result = DeriveActualIndentationFromList(containingList, index, sourceFile, options);
                if (result != -1)
                {
                    return result;
                }
            }
            int delta = listIndentsChild ? options.IndentSize : 0;
            int res = GetActualIndentationForListStartLine(containingList, sourceFile, options);
            return res == -1 ? delta : res + delta;
        }
        return -1;
    }

    private static int GetActualIndentationForListStartLine(NodeList? list, SourceFile sourceFile, FormatCodeSettings options)
    {
        if (list is null)
        {
            return -1;
        }
        (int line, int character) = global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(sourceFile, list.Loc.Pos);
        return FindColumnForFirstNonWhitespaceCharacterInLine(line, character, sourceFile, options);
    }

    private static int DeriveActualIndentationFromList(NodeList list, int index, SourceFile sourceFile, FormatCodeSettings options)
    {
        global::Ts.Core.Debug.Assert(index >= 0 && index < list.Nodes.Length);

        Node node = list.Nodes[index];
        (int line, int character) = GetStartLineAndCharacterForNode(node, sourceFile);

        for (int i = index; i >= 0; i--)
        {
            if (list.Nodes[i].Kind == SyntaxKind.CommaToken)
            {
                continue;
            }
            int previousEndLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, list.Nodes[i].End);
            if (previousEndLine != line)
            {
                return FindColumnForFirstNonWhitespaceCharacterInLine(line, character, sourceFile, options);
            }

            (line, character) = GetStartLineAndCharacterForNode(list.Nodes[i], sourceFile);
        }
        return -1;
    }

    private static int FindColumnForFirstNonWhitespaceCharacterInLine(int line, int character, SourceFile sourceFile, FormatCodeSettings options)
    {
        int lineStart = global::Ts.Scanner.Scanner.GetECMAPositionOfLineAndByteOffset(sourceFile, line, 0);
        return FindFirstNonWhitespaceColumn(lineStart, lineStart + character, sourceFile, options);
    }

    internal static int FindFirstNonWhitespaceColumn(int startPos, int endPos, SourceFile sourceFile, FormatCodeSettings options)
    {
        (_, int column) = FindFirstNonWhitespaceCharacterAndColumn(startPos, endPos, sourceFile, options);
        return column;
    }

    /**
    * Character is the actual index of the character since the beginning of the line.
    * Column - position of the character after expanding tabs to spaces.
    */
    internal static (int Character, int Column) FindFirstNonWhitespaceCharacterAndColumn(
        int startPos,
        int endPos,
        SourceFile sourceFile,
        FormatCodeSettings options)
    {
        int column = 0;
        string text = sourceFile.Text();
        int pos = startPos;
        while (pos < endPos)
        {
            char ch = text[pos];
            if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(ch))
            {
                break;
            }

            if (ch == '\t')
            {
                if (options.TabSize > 0)
                {
                    // Preserve the Go/tsgo formula exactly.
                    column += options.TabSize + (column % options.TabSize);
                }
            }
            else
            {
                column++;
            }

            pos++;
        }
        return (pos - startPos, column);
    }

    internal static bool ChildStartsOnTheSameLineWithElseInIfStatement(Node parent, Node child, int childStartLine, SourceFile sourceFile)
    {
        if (parent.Kind == SyntaxKind.IfStatement && parent.AsIfStatement().ElseStatement == child)
        {
            Node? elseKeyword = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, child.Pos);
            global::Ts.Core.Debug.Assert(elseKeyword is not null);
            int elseKeywordStartLine = GetStartLineForNode(elseKeyword!, sourceFile);
            return elseKeywordStartLine == childStartLine;
        }
        return false;
    }

    private static (int Line, int Character) GetStartLineAndCharacterForNode(Node n, SourceFile sourceFile)
    {
        return global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(
            sourceFile,
            global::Ts.Scanner.Scanner.GetTokenPosOfNode(n, sourceFile, false));
    }

    private static int GetStartLineForNode(Node n, SourceFile sourceFile)
    {
        return global::Ts.Scanner.Scanner.GetECMALineOfPosition(
            sourceFile,
            global::Ts.Scanner.Scanner.GetTokenPosOfNode(n, sourceFile, false));
    }

    internal static NodeList? GetContainingList(Node node, SourceFile sourceFile)
    {
        if (node.Parent is null)
        {
            return null;
        }
        return GetListByRange(global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, sourceFile, false), node.End, node.Parent, sourceFile);
    }

    private static NodeList? GetListByRange(int start, int end, Node node, SourceFile sourceFile)
    {
        var range = new TextRange(start, end);
        switch (node.Kind)
        {
            case SyntaxKind.TypeReference:
                return GetList(node.TypeArgumentList(), range, node, sourceFile);
            case SyntaxKind.ObjectLiteralExpression:
                return GetList(node.PropertyList(), range, node, sourceFile);
            case SyntaxKind.ArrayLiteralExpression:
                return GetList(node.ElementList(), range, node, sourceFile);
            case SyntaxKind.TypeLiteral:
                return GetList(node.MemberList(), range, node, sourceFile);
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.CallSignature:
            case SyntaxKind.Constructor:
            case SyntaxKind.ConstructorType:
            case SyntaxKind.ConstructSignature:
                NodeList? typeParameters = GetList(node.TypeParameterList(), range, node, sourceFile);
                return typeParameters ?? GetList(node.ParameterList(), range, node, sourceFile);
            case SyntaxKind.GetAccessor:
                return GetList(node.ParameterList(), range, node, sourceFile);
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSDocTemplateTag:
                return GetList(node.TypeParameterList(), range, node, sourceFile);
            case SyntaxKind.NewExpression:
            case SyntaxKind.CallExpression:
                NodeList? typeArguments = GetList(node.TypeArgumentList(), range, node, sourceFile);
                return typeArguments ?? GetList(node.ArgumentList(), range, node, sourceFile);
            case SyntaxKind.VariableDeclarationList:
                return GetList(node.AsVariableDeclarationList().Declarations, range, node, sourceFile);
            case SyntaxKind.ObjectBindingPattern:
            case SyntaxKind.ArrayBindingPattern:
            case SyntaxKind.NamedImports:
            case SyntaxKind.NamedExports:
                return GetList(node.ElementList(), range, node, sourceFile);
            default:
                return null; // TODO: should this be a panic? It isn't in strada.
        }
    }

    private static NodeList? GetList(NodeList? list, TextRange range, Node node, SourceFile sourceFile)
    {
        if (list is null)
        {
            return null;
        }
        return range.ContainedBy(GetVisualListRange(node, list.Loc, sourceFile)) ? list : null;
    }

    private static TextRange GetVisualListRange(Node node, TextRange list, SourceFile sourceFile)
    {
        Node? prior = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, list.Pos);
        int priorEnd = prior?.End ?? list.Pos;
        global::Ts.Scanner.Scanner scanner = global::Ts.Scanner.Scanner.GetScannerForSourceFile(sourceFile, list.End);
        int nextStart = scanner.Token() == SyntaxKind.EndOfFile ? list.End : scanner.TokenStart();
        return new TextRange(priorEnd, nextStart);
    }

    private static (int Line, int Character) GetContainingListOrParentStart(Node parent, Node child, SourceFile sourceFile)
    {
        NodeList? containingList = GetContainingList(child, sourceFile);
        int startPos = containingList?.Loc.Pos ?? global::Ts.Scanner.Scanner.GetTokenPosOfNode(parent, sourceFile, false);
        return global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(sourceFile, startPos);
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

    internal static bool ChildIsUnindentedBranchOfConditionalExpression(Node parent, Node child, int childStartLine, SourceFile sourceFile)
    {
        if (parent.Kind == SyntaxKind.ConditionalExpression &&
            (child == parent.AsConditionalExpression().WhenTrue || child == parent.AsConditionalExpression().WhenFalse))
        {
            int conditionEndLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(
                sourceFile,
                parent.AsConditionalExpression().Condition!.End);
            if (child == parent.AsConditionalExpression().WhenTrue)
            {
                return childStartLine == conditionEndLine;
            }

            // On the whenFalse side, look at whenTrue because if it was indented,
            // whenFalse must also be indented.
            Node whenTrue = parent.AsConditionalExpression().WhenTrue!;
            int trueStartLine = GetStartLineForNode(whenTrue, sourceFile);
            int trueEndLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, whenTrue.End);
            return conditionEndLine == trueStartLine && trueEndLine == childStartLine;
        }
        return false;
    }

    internal static bool ArgumentStartsOnSameLineAsPreviousArgument(Node parent, Node child, int childStartLine, SourceFile sourceFile)
    {
        if (parent.Kind == SyntaxKind.CallExpression || parent.Kind == SyntaxKind.NewExpression)
        {
            Node[]? arguments = parent.Arguments();
            if (arguments is null || arguments.Length == 0)
            {
                return false;
            }
            int currentIndex = Array.IndexOf(arguments, child);
            if (currentIndex <= 0)
            {
                return false;
            }

            Node previousNode = arguments[currentIndex - 1];
            int lineOfPreviousNode = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, previousNode.End);
            return childStartLine == lineOfPreviousNode;
        }
        return false;
    }
}
