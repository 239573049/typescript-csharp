// Ported from tsgo internal/parser/parser.go — Jsx cluster (JSX element / self-closing /
// fragment parsing, attributes, spread attributes, expression containers, children, JSX text,
// and the JSX-mode scanner shims). Positions are UTF-16 code units (plan/01). Deep recursion is
// reached only under the LargeStackExecutor entry (Parser.ParseSourceFile, R01).
//
// Methods for other clusters (Expressions/Types/Declarations/Statements) referenced here are
// implemented in their own Parser.*.cs partials; this file only calls them. The ast free function
// TagNamesAreEquivalent is provided by the Jsx cluster's gap file (Ast/Parser.Jsx.Gaps.cs).
using System;
using System.Collections.Generic;

using Ts.Ast;
using Ts.Core;

using Message = Ts.Diagnostics.Message;

namespace Ts.Parser;

public sealed partial class Parser
{
    // ── Test seam (Ts.Compiler.Tests via InternalsVisibleTo; 05-conventions.md §16) ──
    // Parses a single top-level JSX expression in JSX language-variant mode. The full ParseSourceFile
    // path also reaches JSX for .tsx sources, but this shallow hook exercises the JSX surface directly
    // without depending on statement-level start-of-expression heuristics. Runs on the 64MB large-stack
    // thread to honor the deep-recursion entry rule (AGENTS §8 / R01).
    internal static (Node Node, Diagnostic[] Diagnostics) ParseJsxExpressionForTest(string sourceText, ScriptKind scriptKind = ScriptKind.Tsx)
    {
        return LargeStackExecutor.Run(() =>
        {
            Parser p = GetParser();
            try
            {
                p.InitializeState(default, sourceText, scriptKind);
                p.NextToken();
                Node node = p.ParseExpression();
                return (node, p.diagnostics.ToArray());
            }
            finally
            {
                PutParser(p);
            }
        });
    }

    private Node ParseJsxElementOrSelfClosingElementOrFragment(bool inExpressionContext, int topInvalidNodePosition, Node? openingTag, bool mustBeUnary)
    {
        int pos = NodePos();
        Node opening = ParseJsxOpeningOrSelfClosingElementOrOpeningFragment(inExpressionContext);
        Node result;
        switch (opening.Kind)
        {
            case SyntaxKind.JsxOpeningElement:
                NodeList children = ParseJsxChildren(opening);
                Node? closingElement;
                Node? lastChild = CoreUtil.LastOrNil(children.Nodes);
                if (lastChild != null && lastChild.Kind == SyntaxKind.JsxElement &&
                    !JsxParserAstGaps.TagNamesAreEquivalent(lastChild.AsJsxElement().OpeningElement!.TagName()!, lastChild.AsJsxElement().ClosingElement!.TagName()!) &&
                    JsxParserAstGaps.TagNamesAreEquivalent(opening.TagName()!, lastChild.AsJsxElement().ClosingElement!.TagName()!))
                {
                    // when an unclosed JsxOpeningElement incorrectly parses its parent's JsxClosingElement,
                    // restructure (<div>(...<span>...</div>)) --> (<div>(...<span>...</>)</div>)
                    // (no need to error; the parent will error)
                    int end = lastChild.Children()!.End;
                    Node missingIdentifier = FinishNodeWithEnd(NewIdentifier(""), end, end);
                    Node newClosingElement = FinishNodeWithEnd(factory.NewJsxClosingElement(missingIdentifier), end, end);
                    Node newLast = FinishNodeWithEnd(
                        factory.NewJsxElement(lastChild.AsJsxElement().OpeningElement, lastChild.Children(), newClosingElement),
                        lastChild.AsJsxElement().OpeningElement!.Pos,
                        end);
                    // force reset parent pointers from discarded parse result
                    if (lastChild.AsJsxElement().OpeningElement != null)
                    {
                        lastChild.AsJsxElement().OpeningElement!.Parent = newLast;
                    }
                    if (lastChild.Children() != null)
                    {
                        foreach (Node c in lastChild.Children()!.Nodes)
                        {
                            c.Parent = newLast;
                        }
                    }
                    newClosingElement.Parent = newLast;
                    Node[] oldNodes = children.Nodes;
                    var replacedNodes = new Node[oldNodes.Length];
                    Array.Copy(oldNodes, replacedNodes, oldNodes.Length - 1);
                    replacedNodes[oldNodes.Length - 1] = newLast;
                    children = NewNodeList(new TextRange(children.Pos, newLast.End), replacedNodes);
                    closingElement = lastChild.AsJsxElement().ClosingElement;
                }
                else
                {
                    closingElement = ParseJsxClosingElement(opening, inExpressionContext);
                    if (!JsxParserAstGaps.TagNamesAreEquivalent(opening.TagName()!, closingElement.TagName()!))
                    {
                        if (openingTag != null && NodeTests.IsJsxOpeningElement(openingTag) && JsxParserAstGaps.TagNamesAreEquivalent(closingElement.TagName()!, openingTag.TagName()!))
                        {
                            // opening incorrectly matched with its parent's closing -- put error on opening
                            ParseErrorAtRange(opening.TagName()!.Loc, global::Ts.Diagnostics.Diagnostics.JSX_element_0_has_no_corresponding_closing_tag, global::Ts.Scanner.Scanner.GetTextOfNodeFromSourceText(sourceText, opening.TagName()!, false /*includeTrivia*/));
                        }
                        else
                        {
                            // other opening/closing mismatches -- put error on closing
                            ParseErrorAtRange(closingElement.TagName()!.Loc, global::Ts.Diagnostics.Diagnostics.Expected_corresponding_JSX_closing_tag_for_0, global::Ts.Scanner.Scanner.GetTextOfNodeFromSourceText(sourceText, opening.TagName()!, false /*includeTrivia*/));
                        }
                    }
                }
                result = FinishNode(factory.NewJsxElement(opening, children, closingElement), pos);
                closingElement!.Parent = result; // force reset parent pointers from possibly discarded parse result
                break;
            case SyntaxKind.JsxOpeningFragment:
                result = FinishNode(factory.NewJsxFragment(opening, ParseJsxChildren(opening), ParseJsxClosingFragment(inExpressionContext)), pos);
                break;
            case SyntaxKind.JsxSelfClosingElement:
                // Nothing else to do for self-closing elements
                result = opening;
                break;
            default:
                throw new InvalidOperationException("Unhandled case in parseJsxElementOrSelfClosingElementOrFragment");
        }
        // If the user writes the invalid code '<div></div><div></div>' in an expression context (i.e. not wrapped in
        // an enclosing tag), we'll naively try to parse   ^ this as a 'less than' operator and the remainder of the tag
        // as garbage, which will cause the formatter to badly mangle the JSX. Perform a speculative parse of a JSX
        // element if we see a < token so that we can wrap it in a synthetic binary expression so the formatter
        // does less damage and we can report a better error.
        // Since JSX elements are invalid < operands anyway, this lookahead parse will only occur in error scenarios
        // of one sort or another.
        // If we are in a unary context, we can't do this recovery; the binary expression we return here is not
        // a valid UnaryExpression and will cause problems later.
        if (!mustBeUnary && inExpressionContext && token == SyntaxKind.LessThanToken)
        {
            int topBadPos = topInvalidNodePosition;
            if (topBadPos < 0)
            {
                topBadPos = result.Pos;
            }
            Node invalidElement = ParseJsxElementOrSelfClosingElementOrFragment(true /*inExpressionContext*/, topBadPos, null, false);
            Node operatorToken = factory.NewToken(SyntaxKind.CommaToken);
            operatorToken.Loc = new TextRange(invalidElement.Pos, invalidElement.Pos);
            ParseErrorAt(global::Ts.Scanner.Scanner.SkipTrivia(sourceText, topBadPos), invalidElement.End, global::Ts.Diagnostics.Diagnostics.JSX_expressions_must_have_one_parent_element);
            result = FinishNode(factory.NewBinaryExpression(null /*modifiers*/, result, null /*typeNode*/, operatorToken, invalidElement), pos);
        }
        return result;
    }

    private NodeList ParseJsxChildren(Node? openingTag)
    {
        int pos = NodePos();
        int saveParsingContexts = parsingContexts;
        parsingContexts |= 1 << (int)ParsingContext.JsxChildren;
        var list = new List<Node>();
        while (true)
        {
            SyntaxKind currentToken = scanner.ReScanJsxToken(true /*allowMultilineJsxText*/);
            Node? child = ParseJsxChild(openingTag, currentToken);
            if (child == null)
            {
                break;
            }
            list.Add(child);
            if (NodeTests.IsJsxOpeningElement(openingTag!) && child.Kind == SyntaxKind.JsxElement &&
                !JsxParserAstGaps.TagNamesAreEquivalent(child.AsJsxElement().OpeningElement!.TagName()!, child.AsJsxElement().ClosingElement!.TagName()!) &&
                JsxParserAstGaps.TagNamesAreEquivalent(openingTag!.TagName()!, child.AsJsxElement().ClosingElement!.TagName()!))
            {
                // stop after parsing a mismatched child like <div>...(<span></div>) in order to reattach the </div> higher
                break;
            }
        }
        parsingContexts = saveParsingContexts;
        return NewNodeList(new TextRange(pos, NodePos()), list.ToArray());
    }

    private Node? ParseJsxChild(Node? openingTag, SyntaxKind token)
    {
        switch (token)
        {
            case SyntaxKind.EndOfFile:
                // If we hit EOF, issue the error at the tag that lacks the closing element
                // rather than at the end of the file (which is useless)
                if (NodeTests.IsJsxOpeningFragment(openingTag!))
                {
                    ParseErrorAtRange(openingTag!.Loc, global::Ts.Diagnostics.Diagnostics.JSX_fragment_has_no_corresponding_closing_tag);
                }
                else
                {
                    // We want the error span to cover only 'Foo.Bar' in < Foo.Bar >
                    // or to cover only 'Foo' in < Foo >
                    Node tag = openingTag!.TagName()!;
                    int start = Math.Min(global::Ts.Scanner.Scanner.SkipTrivia(sourceText, tag.Pos), tag.End);
                    ParseErrorAt(start, tag.End, global::Ts.Diagnostics.Diagnostics.JSX_element_0_has_no_corresponding_closing_tag,
                        global::Ts.Scanner.Scanner.GetTextOfNodeFromSourceText(sourceText, openingTag!.TagName()!, false /*includeTrivia*/));
                }
                return null;
            case SyntaxKind.LessThanSlashToken:
            case SyntaxKind.ConflictMarkerTrivia:
                return null;
            case SyntaxKind.JsxText:
            case SyntaxKind.JsxTextAllWhiteSpaces:
                return ParseJsxText();
            case SyntaxKind.OpenBraceToken:
                return ParseJsxExpression(false /*inExpressionContext*/);
            case SyntaxKind.LessThanToken:
                return ParseJsxElementOrSelfClosingElementOrFragment(false /*inExpressionContext*/, -1 /*topInvalidNodePosition*/, openingTag, false);
        }
        throw new InvalidOperationException("Unhandled case in parseJsxChild");
    }

    private Node ParseJsxText()
    {
        int pos = NodePos();
        Node result = factory.NewJsxText(scanner.TokenValue(), token == SyntaxKind.JsxTextAllWhiteSpaces);
        ScanJsxText();
        return FinishNode(result, pos);
    }

    private Node? ParseJsxExpression(bool inExpressionContext)
    {
        int pos = NodePos();
        if (!ParseExpected(SyntaxKind.OpenBraceToken))
        {
            return null;
        }
        Node? dotDotDotToken = null;
        Node? expression = null;
        if (token != SyntaxKind.CloseBraceToken)
        {
            if (!inExpressionContext)
            {
                dotDotDotToken = ParseOptionalToken(SyntaxKind.DotDotDotToken);
            }
            // Only an AssignmentExpression is valid here per the JSX spec,
            // but we can unambiguously parse a comma sequence and provide
            // a better error message in grammar checking.
            expression = ParseExpression();
        }
        if (inExpressionContext)
        {
            ParseExpected(SyntaxKind.CloseBraceToken);
        }
        else if (ParseExpectedWithoutAdvancing(SyntaxKind.CloseBraceToken))
        {
            ScanJsxText();
        }
        return FinishNode(factory.NewJsxExpression(dotDotDotToken, expression), pos);
    }

    private SyntaxKind ScanJsxText()
    {
        token = scanner.ScanJsxToken();
        return token;
    }

    private SyntaxKind ScanJsxIdentifier()
    {
        token = scanner.ScanJsxIdentifier();
        return token;
    }

    private SyntaxKind ScanJsxAttributeValue()
    {
        token = scanner.ScanJsxAttributeValue();
        return token;
    }

    private Node ParseJsxClosingElement(Node? open, bool inExpressionContext)
    {
        int pos = NodePos();
        ParseExpected(SyntaxKind.LessThanSlashToken);
        Node tagName = ParseJsxElementName();
        if (ParseExpectedWithDiagnostic(SyntaxKind.GreaterThanToken, null /*diagnosticMessage*/, false /*shouldAdvance*/))
        {
            // manually advance the scanner in order to look for jsx text inside jsx
            if (inExpressionContext || !JsxParserAstGaps.TagNamesAreEquivalent(open!.TagName()!, tagName))
            {
                NextToken();
            }
            else
            {
                ScanJsxText();
            }
        }
        return FinishNode(factory.NewJsxClosingElement(tagName), pos);
    }

    private Node ParseJsxOpeningOrSelfClosingElementOrOpeningFragment(bool inExpressionContext)
    {
        int pos = NodePos();
        ParseExpected(SyntaxKind.LessThanToken);
        if (token == SyntaxKind.GreaterThanToken)
        {
            // See below for explanation of scanJsxText
            ScanJsxText();
            return FinishNode(factory.NewJsxOpeningFragment(), pos);
        }
        Node tagName = ParseJsxElementName();
        NodeList? typeArguments = null;
        if ((contextFlags & NodeFlags.JavaScriptFile) == 0)
        {
            typeArguments = ParseTypeArguments();
        }
        Node attributes = ParseJsxAttributes();
        Node result;
        if (token == SyntaxKind.GreaterThanToken)
        {
            // Closing tag, so scan the immediately-following text with the JSX scanning instead
            // of regular scanning to avoid treating illegal characters (e.g. '#') as immediate
            // scanning errors
            ScanJsxText();
            result = factory.NewJsxOpeningElement(tagName, typeArguments, attributes);
        }
        else
        {
            ParseExpected(SyntaxKind.SlashToken);
            if (ParseExpectedWithoutAdvancing(SyntaxKind.GreaterThanToken))
            {
                if (inExpressionContext)
                {
                    NextToken();
                }
                else
                {
                    ScanJsxText();
                }
            }
            result = factory.NewJsxSelfClosingElement(tagName, typeArguments, attributes);
        }
        return FinishNode(result, pos);
    }

    private Node ParseJsxElementName()
    {
        int pos = NodePos();
        // JsxElement can have name in the form of
        //      propertyAccessExpression
        //      primaryExpression in the form of an identifier and "this" keyword
        // We can't just simply use parseLeftHandSideExpressionOrHigher because then we will start consider class,function etc as a keyword
        // We only want to consider "this" as a primaryExpression
        Node initialExpression = ParseJsxTagName();
        if (NodeTests.IsJsxNamespacedName(initialExpression))
        {
            return initialExpression; // `a:b.c` is invalid syntax, don't even look for the `.` if we parse `a:b`, and let `parseAttribute` report "unexpected :" instead.
        }
        Node expression = initialExpression;
        while (ParseOptional(SyntaxKind.DotToken))
        {
            expression = FinishNode(factory.NewPropertyAccessExpression(expression, null, ParseRightSideOfDot(true /*allowIdentifierNames*/, false /*allowPrivateIdentifiers*/, false /*allowUnicodeEscapeSequenceInIdentifierName*/), NodeFlags.None), pos);
        }
        return expression;
    }

    private Node ParseJsxTagName()
    {
        int pos = NodePos();
        ScanJsxIdentifier();
        bool isThis = token == SyntaxKind.ThisKeyword;
        Node tagName = ParseIdentifierNameErrorOnUnicodeEscapeSequence();
        if (ParseOptional(SyntaxKind.ColonToken))
        {
            ScanJsxIdentifier();
            return FinishNode(factory.NewJsxNamespacedName(tagName, ParseIdentifierNameErrorOnUnicodeEscapeSequence()), pos);
        }
        if (isThis)
        {
            Node result = factory.NewKeywordExpression(SyntaxKind.ThisKeyword);
            return FinishNode(result, pos);
        }
        return tagName;
    }

    private Node ParseJsxAttributes()
    {
        int pos = NodePos();
        return FinishNode(factory.NewJsxAttributes(ParseList(ParsingContext.JsxAttributes, ParseJsxAttribute)), pos);
    }

    private Node ParseJsxAttribute()
    {
        if (token == SyntaxKind.OpenBraceToken)
        {
            return ParseJsxSpreadAttribute();
        }
        int pos = NodePos();
        return FinishNode(factory.NewJsxAttribute(ParseJsxAttributeName(), ParseJsxAttributeValue()), pos);
    }

    private Node ParseJsxSpreadAttribute()
    {
        int pos = NodePos();
        ParseExpected(SyntaxKind.OpenBraceToken);
        ParseExpected(SyntaxKind.DotDotDotToken);
        Node expression = ParseExpression();
        ParseExpected(SyntaxKind.CloseBraceToken);
        return FinishNode(factory.NewJsxSpreadAttribute(expression), pos);
    }

    private Node ParseJsxAttributeName()
    {
        int pos = NodePos();
        ScanJsxIdentifier();
        Node attrName = ParseIdentifierNameErrorOnUnicodeEscapeSequence();
        if (ParseOptional(SyntaxKind.ColonToken))
        {
            ScanJsxIdentifier();
            return FinishNode(factory.NewJsxNamespacedName(attrName, ParseIdentifierNameErrorOnUnicodeEscapeSequence()), pos);
        }
        return attrName;
    }

    private Node? ParseJsxAttributeValue()
    {
        if (token == SyntaxKind.EqualsToken)
        {
            if (ScanJsxAttributeValue() == SyntaxKind.StringLiteral)
            {
                return ParseLiteralExpression(false /*intern*/);
            }
            if (token == SyntaxKind.OpenBraceToken)
            {
                return ParseJsxExpression(true /*inExpressionContext*/);
            }
            if (token == SyntaxKind.LessThanToken)
            {
                return ParseJsxElementOrSelfClosingElementOrFragment(true /*inExpressionContext*/, -1, null, false);
            }
            ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_or_JSX_element_expected);
        }
        return null;
    }

    private Node ParseJsxClosingFragment(bool inExpressionContext)
    {
        int pos = NodePos();
        ParseExpected(SyntaxKind.LessThanSlashToken);
        if (ParseExpectedWithDiagnostic(SyntaxKind.GreaterThanToken, global::Ts.Diagnostics.Diagnostics.Expected_corresponding_closing_tag_for_JSX_fragment, false /*shouldAdvance*/))
        {
            // manually advance the scanner in order to look for jsx text inside jsx
            if (inExpressionContext)
            {
                NextToken();
            }
            else
            {
                ScanJsxText();
            }
        }
        return FinishNode(factory.NewJsxClosingFragment(), pos);
    }
}
