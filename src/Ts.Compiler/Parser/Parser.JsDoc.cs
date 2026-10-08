// Ported from tsgo internal/parser/jsdoc.go and reparser.go (JSDoc comment/tag grammar,
// reparse of JS JSDoc into TS-shaped AST), plus the parseJSDocType* family from parser.go
// (parser.go:2844-2878) that the codegen assigned to this cluster.
//
// JSDoc spelling preserved throughout (裁决 R6). Positions are UTF-16 code units (plan §6).
// Go arena slices (`nodeSliceArena` / `stringSliceArena` / `*Space` reuse buffers) become plain
// List<T> allocations per AGENTS 铁律5 (arena.New → new T, no pooling).
//
// A handful of sibling helpers (gatherTypeParameters, parseCallbackTagParameters,
// parseTemplateTagTypeParameter[s], parseExpressionWithTypeArgumentsForAugments) are still stubs
// in Parser.Declarations.cs; @template/@callback/@overload/@augments/@implements therefore parse
// but throw at runtime until that cluster lands. Build stays green (calls compile).
using System;
using System.Collections.Generic;
using System.Text;

using Ts.Ast;
using Ts.Core;

using Message = Ts.Diagnostics.Message;

namespace Ts.Parser;

public sealed partial class Parser
{
    // jsdoc.go:39-46
    private enum JsDocState
    {
        BeginningOfLine,
        SawAsterisk,
        SavingComments,
        SavingBackticks,
    }

    // jsdoc.go:107
    private Node ParseJSDocTypeExpression(bool mayOmitBraces)
    {
        int pos = NodePos();
        bool hasBrace;
        if (mayOmitBraces)
        {
            hasBrace = ParseOptional(SyntaxKind.OpenBraceToken);
        }
        else
        {
            hasBrace = ParseExpected(SyntaxKind.OpenBraceToken);
        }
        NodeFlags saveContextFlags = contextFlags;
        SetContextFlags(NodeFlags.JSDoc, true);
        Node t = ParseJSDocType();
        contextFlags = saveContextFlags;
        if (hasBrace)
        {
            ParseExpectedJSDoc(SyntaxKind.CloseBraceToken);
        }
        return FinishNode(factory.NewJSDocTypeExpression(t), pos);
    }

    // jsdoc.go:126
    private Node ParseJSDocNameReference()
    {
        int pos = NodePos();
        bool hasBrace = ParseOptional(SyntaxKind.OpenBraceToken);
        Node? entityName = ParseJSDocLinkName();
        if (hasBrace)
        {
            ParseExpectedJSDoc(SyntaxKind.CloseBraceToken);
        }
        scanner.ResetPos(scanner.TokenFullStart());
        NextTokenJSDoc();
        return FinishNode(factory.NewJSDocNameReference(entityName), pos);
    }

    // Pass end=-1 to parse the text to the end. jsdoc.go:139
    private Node? ParseJSDocComment(Node? parent, int start, int end, int fullStart)
    {
        if (end == -1)
        {
            end = sourceText.Length;
        }
        // Check for /** (JSDoc opening part)
        if (!Utilities.IsJSDocLikeText(sourceText.Substring(start)))
        {
            // TODO: This should be a panic, unless parseSingleJSDocComment is calling this (not ported yet)
            return null;
        }

        string saveSourceText = sourceText;
        SyntaxKind saveToken = token;
        NodeFlags saveContextFlags = contextFlags;
        int saveParsingContexts = parsingContexts;
        global::Ts.Scanner.ScannerState saveScannerState = scanner.Mark();
        int saveDiagnosticsLength = diagnostics.Count;
        bool saveHasParseError = hasParseError;
        bool saveHasAwaitIdentifier = statementHasAwaitIdentifier;

        // initial indent is start+4 to account for leading `/** `
        // + 1 because \n is one character before the first character in the line and,
        // if there is no \n before start, -1 is one index before the first character in the string
        int lastNewline = start > 0 ? sourceText.LastIndexOf('\n', start - 1) : -1;
        int initialIndent = start + 4 - (lastNewline + 1);
        // -2 for trailing `*/`
        sourceText = sourceText.Substring(0, end - 2);
        scanner.SetText(sourceText);
        // +3 for leading `/**`
        scanner.ResetPos(start + 3);
        SetContextFlags(NodeFlags.JSDoc, true);
        parsingContexts |= 1 << (int)ParsingContext.JSDocComment;

        Node comment = ParseJSDocCommentWorker(start, end, fullStart, initialIndent);
        // move jsdoc diagnostics to jsdocDiagnostics -- for JS files only
        if ((contextFlags & NodeFlags.JavaScriptFile) != 0)
        {
            for (int i = saveDiagnosticsLength; i < diagnostics.Count; i++)
            {
                jsdocDiagnostics.Add(diagnostics[i]);
            }
        }
        TruncateTo(diagnostics, saveDiagnosticsLength);

        sourceText = saveSourceText;
        scanner.SetText(sourceText);
        parsingContexts = saveParsingContexts;
        contextFlags = saveContextFlags;
        scanner.Rewind(saveScannerState);
        token = saveToken;
        hasParseError = saveHasParseError;
        statementHasAwaitIdentifier = saveHasAwaitIdentifier;

        return comment;
    }

    // jsdoc.go:193
    // offset: the offset in the containing file.
    // indent: the number of spaces to consider as the margin (applies to non-first lines only).
    private Node ParseJSDocCommentWorker(int start, int end, int fullStart, int indent)
    {
        // Initially we can parse out a tag. We also have seen a starting asterisk.
        // This is so that /** * @type */ doesn't parse.
        var tags = new List<Node>();
        int tagsPos = -1;
        int tagsEnd = -1;
        var state = JsDocState.SawAsterisk;
        int backtickCount = 0;
        bool inFencedCodeBlock = false;
        var commentParts = new List<Node>();
        var comments = new List<string>();
        int commentsPos = -1;
        int linkEnd = start;
        int margin = -1;
        void PushComment(string text)
        {
            if (margin == -1)
            {
                margin = indent;
            }
            comments.Add(text);
            indent += text.Length;
        }

        NextTokenJSDoc();
        while (ParseOptionalJsdoc(SyntaxKind.WhitespaceTrivia))
        {
        }
        if (ParseOptionalJsdoc(SyntaxKind.NewLineTrivia))
        {
            state = JsDocState.BeginningOfLine;
            indent = 0;
        }
        while (true)
        {
            // Detect fenced code blocks by counting consecutive backtick tokens.
            // Three or more consecutive backticks toggle the fenced code block state.
            if (token != SyntaxKind.BacktickToken && backtickCount > 0)
            {
                if (backtickCount >= 3)
                {
                    inFencedCodeBlock = !inFencedCodeBlock;
                }
                backtickCount = 0;
            }
            switch (token)
            {
                case SyntaxKind.AtToken:
                    if (inFencedCodeBlock || !scanner.CanFollowJSDocAt())
                    {
                        if (inFencedCodeBlock)
                        {
                            state = JsDocState.SavingBackticks;
                        }
                        else
                        {
                            state = JsDocState.SavingComments;
                        }
                        PushComment(scanner.TokenText());
                        break;
                    }
                    comments = RemoveTrailingWhitespace(comments);
                    if (commentsPos == -1)
                    {
                        commentsPos = NodePos();
                    }
                    Node tag = ParseTag(tags.ToArray(), indent);
                    if (tagsPos == -1)
                    {
                        tagsPos = tag.Pos;
                    }
                    tags.Add(tag);
                    tagsEnd = tag.End;
                    // NOTE: According to usejsdoc.org, a tag goes to end of line, except the last tag.
                    // Real-world comments may break this rule, so "BeginningOfLine" will not be a real line
                    // beginning for malformed examples like `/** @param {string} x @returns {number} */`
                    state = JsDocState.BeginningOfLine;
                    margin = -1;
                    break;
                case SyntaxKind.NewLineTrivia:
                    comments.Add(scanner.TokenText());
                    state = JsDocState.BeginningOfLine;
                    indent = 0;
                    break;
                case SyntaxKind.AsteriskToken:
                    string asterisk = scanner.TokenText();
                    if (state == JsDocState.SawAsterisk)
                    {
                        // If we've already seen an asterisk, then we can no longer parse a tag on this line
                        state = JsDocState.SavingComments;
                        PushComment(asterisk);
                    }
                    else
                    {
                        if (state != JsDocState.BeginningOfLine)
                        {
                            throw new InvalidOperationException("state must be BeginningOfLine");
                        }
                        // Ignore the first asterisk on a line
                        state = JsDocState.SawAsterisk;
                        indent += asterisk.Length;
                    }
                    break;
                case SyntaxKind.WhitespaceTrivia:
                    if (state == JsDocState.SavingComments || state == JsDocState.SavingBackticks)
                    {
                        throw new InvalidOperationException("whitespace shouldn't come from the scanner while saving top-level comment text");
                    }
                    // only collect whitespace if we're already saving comments or have just crossed the margin
                    string whitespace = scanner.TokenText();
                    if (margin > -1 && indent + whitespace.Length > margin)
                    {
                        int existingIndent = margin - indent;
                        if (existingIndent < 0)
                        {
                            existingIndent += whitespace.Length;
                        }
                        if (existingIndent < 0)
                        {
                            existingIndent = 0;
                        }
                        comments.Add(whitespace.Substring(existingIndent));
                    }
                    indent += whitespace.Length;
                    break;
                case SyntaxKind.EndOfFile:
                    goto endLoop;
                case SyntaxKind.JSDocCommentTextToken:
                    if (state != JsDocState.SavingBackticks)
                    {
                        state = inFencedCodeBlock ? JsDocState.SavingBackticks : JsDocState.SavingComments;
                    }
                    PushComment(scanner.TokenValue());
                    break;
                case SyntaxKind.BacktickToken:
                    backtickCount++;
                    if (state == JsDocState.SavingBackticks)
                    {
                        state = JsDocState.SavingComments;
                    }
                    else
                    {
                        state = JsDocState.SavingBackticks;
                    }
                    PushComment(scanner.TokenText());
                    break;
                case SyntaxKind.OpenBraceToken:
                    if (inFencedCodeBlock)
                    {
                        state = JsDocState.SavingBackticks;
                        PushComment(scanner.TokenText());
                        break;
                    }
                    state = JsDocState.SavingComments;
                    int commentEnd = scanner.TokenFullStart();
                    int linkStart = scanner.TokenEnd() - 1;
                    Node? link = ParseJSDocLink(linkStart);
                    if (link != null)
                    {
                        if (linkEnd == start)
                        {
                            comments = RemoveLeadingNewlines(comments);
                        }
                        Node jsdocText = FinishNodeWithEnd(factory.NewJSDocText(comments.ToArray()), linkEnd, commentEnd);
                        commentParts.Add(jsdocText);
                        commentParts.Add(link);
                        comments.Clear();
                        linkEnd = scanner.TokenEnd();
                        break;
                    }
                    goto default;
                default:
                    // Anything else is doc comment text. We just save it. Because it wasn't a tag, we can no
                    // longer parse a tag on this line until we hit the next line break.
                    if (state != JsDocState.SavingBackticks)
                    {
                        state = inFencedCodeBlock ? JsDocState.SavingBackticks : JsDocState.SavingComments;
                    }
                    PushComment(scanner.TokenText());
                    break;
            }
            if (state == JsDocState.SavingComments || state == JsDocState.SavingBackticks)
            {
                NextJSDocCommentTextToken(state == JsDocState.SavingBackticks);
            }
            else
            {
                NextTokenJSDoc();
            }
        }
    endLoop:

        if (commentsPos == -1)
        {
            commentsPos = scanner.TokenFullStart();
        }

        if (comments.Count > 0)
        {
            comments[^1] = TrimRightUnicodeSpace(comments[^1]);
            Node jsdocText = FinishNodeWithEnd(factory.NewJSDocText(comments.ToArray()), linkEnd, commentsPos);
            commentParts.Add(jsdocText);
        }

        if (commentParts.Count > 0 && tags.Count > 0 && commentsPos == -1)
        {
            throw new InvalidOperationException("having parsed tags implies that the end of the comment span should be set");
        }

        NodeList? tagsNodeList = null;
        if (tagsPos != -1)
        {
            tagsNodeList = NewNodeList(new TextRange(tagsPos, tagsEnd), tags.ToArray());
        }

        Node jsdocComment = factory.NewJSDoc(
            NewNodeList(new TextRange(start, commentsPos), commentParts.ToArray()),
            tagsNodeList);
        return FinishNodeWithEnd(jsdocComment, fullStart, end);
    }

    // jsdoc.go:380
    private static List<string> RemoveLeadingNewlines(List<string> comments)
    {
        int i = 0;
        while (i < comments.Count && comments[i].TrimStart('\r', '\n').Length == 0)
        {
            i++;
        }
        if (i > 0)
        {
            comments.RemoveRange(0, i);
        }
        return comments;
    }

    // jsdoc.go:388 — strings.TrimRightFunc(s, stringutil.IsWhiteSpaceLike)
    private static string TrimEnd(string s)
    {
        int end = s.Length;
        while (end > 0 && global::Ts.StringUtil.StringUtil.IsWhiteSpaceLike(s[end - 1]))
        {
            end--;
        }
        return end == s.Length ? s : s.Substring(0, end);
    }

    // jsdoc.go:359 uses unicode.IsSpace for the final comment's trailing trim.
    private static string TrimRightUnicodeSpace(string s)
    {
        int end = s.Length;
        while (end > 0 && char.IsWhiteSpace(s[end - 1]))
        {
            end--;
        }
        return end == s.Length ? s : s.Substring(0, end);
    }

    // jsdoc.go:392
    private static List<string> RemoveTrailingWhitespace(List<string> comments)
    {
        int end = comments.Count;
        for (int i = comments.Count - 1; i >= 0; i--)
        {
            string trimmed = TrimEnd(comments[i]);
            if (trimmed == "")
            {
                end = i;
            }
            else
            {
                comments[i] = trimmed;
                break;
            }
        }
        if (end < comments.Count)
        {
            comments.RemoveRange(end, comments.Count - end);
        }
        return comments;
    }

    // jsdoc.go:431
    private string SkipWhitespaceOrAsterisk()
    {
        if (token == SyntaxKind.WhitespaceTrivia || token == SyntaxKind.NewLineTrivia)
        {
            if (LookAhead(IsNextNonwhitespaceTokenEndOfFile))
            {
                // Don't skip whitespace prior to EoF (or end of comment) - that shouldn't be included in any node's range
                return "";
            }
        }

        bool precedingLineBreak = scanner.HasPrecedingLineBreak();
        bool seenLineBreak = false;
        var indents = new List<string>();
        while ((precedingLineBreak && token == SyntaxKind.AsteriskToken) || token == SyntaxKind.WhitespaceTrivia || token == SyntaxKind.NewLineTrivia)
        {
            indents.Add(scanner.TokenText());
            if (token == SyntaxKind.NewLineTrivia)
            {
                precedingLineBreak = true;
                seenLineBreak = true;
                indents.Clear();
            }
            else if (token == SyntaxKind.AsteriskToken)
            {
                precedingLineBreak = false;
            }
            NextTokenJSDoc();
        }
        return seenLineBreak ? string.Concat(indents) : "";
    }

    // jsdoc.go:460
    private Node ParseTag(Node[]? tags, int margin)
    {
        if (token != SyntaxKind.AtToken)
        {
            throw new InvalidOperationException("should be called only at the start of a tag");
        }
        int start = scanner.TokenStart();
        NextTokenJSDoc();

        Node tagName = ParseJSDocIdentifierName(global::Ts.Diagnostics.Diagnostics.Identifier_expected);
        string indentText = SkipWhitespaceOrAsterisk();

        Node? tag;
        switch (tagName.Text())
        {
            case "implements":
                tag = ParseImplementsTag(start, tagName, margin, indentText);
                break;
            case "augments":
            case "extends":
                tag = ParseAugmentsTag(start, tagName, margin, indentText);
                break;
            case "public":
                tag = ParseSimpleTag(start, (tn, comment) => factory.NewJSDocPublicTag(tn, comment), tagName, margin, indentText);
                break;
            case "private":
                tag = ParseSimpleTag(start, (tn, comment) => factory.NewJSDocPrivateTag(tn, comment), tagName, margin, indentText);
                break;
            case "protected":
                tag = ParseSimpleTag(start, (tn, comment) => factory.NewJSDocProtectedTag(tn, comment), tagName, margin, indentText);
                break;
            case "readonly":
                tag = ParseSimpleTag(start, (tn, comment) => factory.NewJSDocReadonlyTag(tn, comment), tagName, margin, indentText);
                break;
            case "override":
                tag = ParseSimpleTag(start, (tn, comment) => factory.NewJSDocOverrideTag(tn, comment), tagName, margin, indentText);
                break;
            case "deprecated":
                hasDeprecatedTag = true;
                tag = ParseSimpleTag(start, (tn, comment) => factory.NewJSDocDeprecatedTag(tn, comment), tagName, margin, indentText);
                break;
            case "this":
                tag = ParseThisTag(start, tagName, margin, indentText);
                break;
            case "arg":
            case "argument":
            case "param":
                tag = ParseParameterOrPropertyTag(start, tagName, PropertyLikeParse.Parameter, margin);
                break;
            case "return":
            case "returns":
                tag = ParseReturnTag(tags, start, tagName, margin, indentText);
                break;
            case "template":
                tag = ParseTemplateTag(start, tagName, margin, indentText);
                break;
            case "type":
                tag = ParseTypeTag(tags, start, tagName, margin, indentText);
                break;
            case "typedef":
                tag = ParseTypedefTag(start, tagName, margin, indentText);
                break;
            case "callback":
                tag = ParseCallbackTag(start, tagName, margin, indentText);
                break;
            case "overload":
                tag = ParseOverloadTag(start, tagName, margin, indentText);
                break;
            case "satisfies":
                tag = ParseSatisfiesTag(start, tagName, margin, indentText);
                break;
            case "see":
                tag = ParseSeeTag(start, tagName, margin, indentText);
                break;
            case "exception":
            case "throws":
                tag = ParseThrowsTag(start, tagName, margin, indentText);
                break;
            case "import":
                tag = ParseImportTag(start, tagName, margin, indentText);
                break;
            default:
                tag = ParseUnknownTag(start, tagName, margin, indentText);
                break;
        }
        if (tag == null)
        {
            throw new InvalidOperationException("tag should not be nil");
        }
        return tag;
    }

    // jsdoc.go:534
    private NodeList? ParseTrailingTagComments(int pos, int end, int margin, string? indentText)
    {
        // some tags, like typedef and callback, have already parsed their comments earlier
        indentText ??= "";
        if (indentText.Length == 0)
        {
            margin += end - pos;
        }
        string? initialMargin = null;
        if (margin < indentText.Length)
        {
            initialMargin = indentText.Substring(margin);
        }
        return ParseTagComments(margin, initialMargin);
    }

    // jsdoc.go:546
    private NodeList? ParseTagComments(int indent, string? initialMargin)
    {
        int commentsPos = NodePos();
        var comments = new List<string>();
        var parts = new List<Node>();
        int linkEnd = -1;
        var state = JsDocState.BeginningOfLine;
        int backtickCount = 0;
        bool inFencedCodeBlock = false;
        if (indent < 0)
        {
            throw new InvalidOperationException("indent must be a natural number");
        }
        int margin = -1;
        void PushComment(string text)
        {
            if (margin == -1)
            {
                margin = indent;
            }
            comments.Add(text);
            indent += text.Length;
        }

        if (initialMargin != null)
        {
            // jump straight to saving comments if there is some initial indentation
            if (initialMargin != "")
            {
                PushComment(initialMargin);
            }
            state = JsDocState.SawAsterisk;
        }
        SyntaxKind tok = token;
        while (true)
        {
            // Detect fenced code blocks by counting consecutive backtick tokens.
            if (tok != SyntaxKind.BacktickToken && backtickCount > 0)
            {
                if (backtickCount >= 3)
                {
                    inFencedCodeBlock = !inFencedCodeBlock;
                }
                backtickCount = 0;
            }
            switch (tok)
            {
                case SyntaxKind.NewLineTrivia:
                    state = JsDocState.BeginningOfLine;
                    // don't use PushComment here because we want to keep the margin unchanged
                    comments.Add(scanner.TokenText());
                    indent = 0;
                    break;
                case SyntaxKind.AtToken:
                    if (!inFencedCodeBlock && scanner.CanFollowJSDocAt())
                    {
                        scanner.ResetPos(scanner.TokenEnd() - 1);
                        goto endLoop;
                    }
                    if (inFencedCodeBlock)
                    {
                        state = JsDocState.SavingBackticks;
                    }
                    else
                    {
                        state = JsDocState.SavingComments;
                    }
                    PushComment(scanner.TokenText());
                    break;
                case SyntaxKind.EndOfFile:
                    goto endLoop;
                case SyntaxKind.WhitespaceTrivia:
                    if (state == JsDocState.SavingComments || state == JsDocState.SavingBackticks)
                    {
                        throw new InvalidOperationException("whitespace shouldn't come from the scanner while saving comment text");
                    }
                    string whitespace = scanner.TokenText();
                    // if the whitespace crosses the margin, take only the whitespace that passes the margin
                    if (margin > -1 && indent + whitespace.Length > margin)
                    {
                        comments.Add(whitespace.Substring(Math.Max(margin - indent, 0)));
                        state = inFencedCodeBlock ? JsDocState.SavingBackticks : JsDocState.SavingComments;
                    }
                    indent += whitespace.Length;
                    break;
                case SyntaxKind.OpenBraceToken:
                    if (inFencedCodeBlock)
                    {
                        state = JsDocState.SavingBackticks;
                        PushComment(scanner.TokenText());
                        break;
                    }
                    state = JsDocState.SavingComments;
                    int commentEnd = scanner.TokenFullStart();
                    int linkStart = scanner.TokenEnd() - 1;
                    Node? link = ParseJSDocLink(linkStart);
                    if (link != null)
                    {
                        int commentStart = linkEnd > -1 ? linkEnd : commentsPos;
                        Node text = FinishNodeWithEnd(factory.NewJSDocText(comments.ToArray()), commentStart, commentEnd);
                        parts.Add(text);
                        parts.Add(link);
                        comments.Clear();
                        linkEnd = scanner.TokenEnd();
                    }
                    else
                    {
                        PushComment(scanner.TokenText());
                    }
                    break;
                case SyntaxKind.BacktickToken:
                    backtickCount++;
                    if (state == JsDocState.SavingBackticks)
                    {
                        state = JsDocState.SavingComments;
                    }
                    else
                    {
                        state = JsDocState.SavingBackticks;
                    }
                    PushComment(scanner.TokenText());
                    break;
                case SyntaxKind.JSDocCommentTextToken:
                    if (state != JsDocState.SavingBackticks)
                    {
                        state = inFencedCodeBlock ? JsDocState.SavingBackticks : JsDocState.SavingComments;
                        // leading identifiers start recording as well
                    }
                    PushComment(scanner.TokenValue());
                    break;
                case SyntaxKind.AsteriskToken:
                    if (state == JsDocState.BeginningOfLine)
                    {
                        // leading asterisks start recording on the *next* (non-whitespace) token
                        state = JsDocState.SawAsterisk;
                        indent += 1;
                        break;
                    }
                    // record the * as a comment
                    goto default;
                default:
                    if (state != JsDocState.SavingBackticks)
                    {
                        state = inFencedCodeBlock ? JsDocState.SavingBackticks : JsDocState.SavingComments;
                        // leading identifiers start recording as well
                    }
                    PushComment(scanner.TokenText());
                    break;
            }
            if (state == JsDocState.SavingComments || state == JsDocState.SavingBackticks)
            {
                tok = NextJSDocCommentTextToken(state == JsDocState.SavingBackticks);
            }
            else
            {
                tok = NextTokenJSDoc();
            }
        }
    endLoop:

        comments = RemoveLeadingNewlines(comments);
        comments = RemoveTrailingWhitespace(comments);
        if (comments.Count > 0)
        {
            int commentStart = linkEnd > -1 ? linkEnd : commentsPos;
            Node text = FinishNode(factory.NewJSDocText(comments.ToArray()), commentStart);
            parts.Add(text);
        }

        if (parts.Count > 0)
        {
            return NewNodeList(new TextRange(commentsPos, scanner.TokenEnd()), parts.ToArray());
        }
        return null;
    }

    // jsdoc.go:714
    private Node? ParseJSDocLink(int start)
    {
        ParserState state = Mark();
        (string linkType, bool ok) = ParseJSDocLinkPrefix();
        if (!ok)
        {
            Rewind(state);
            return null;
        }
        NextTokenJSDoc();
        // start at token after link, then skip any whitespace
        SkipWhitespace();
        Node? name = ParseJSDocLinkName();
        var text = new List<string>();
        while (token != SyntaxKind.CloseBraceToken && token != SyntaxKind.NewLineTrivia && token != SyntaxKind.EndOfFile)
        {
            text.Add(scanner.TokenText());
            NextTokenJSDoc(); // Couldn't this be nextTokenCommentJSDoc?
        }
        Node create;
        switch (linkType)
        {
            case "link":
                create = factory.NewJSDocLink(name, text.ToArray());
                break;
            case "linkcode":
                create = factory.NewJSDocLinkCode(name, text.ToArray());
                break;
            default:
                create = factory.NewJSDocLinkPlain(name, text.ToArray());
                break;
        }
        return FinishNodeWithEnd(create, start, scanner.TokenEnd());
    }

    // jsdoc.go:742
    private Node? ParseJSDocLinkName()
    {
        if (Utilities.TokenIsIdentifierOrKeyword(token))
        {
            int pos = NodePos();
            Node name = ParseIdentifierName();
            while (ParseOptional(SyntaxKind.DotToken))
            {
                Node right;
                if (token == SyntaxKind.PrivateIdentifier)
                {
                    right = CreateMissingIdentifier();
                }
                else
                {
                    right = ParseIdentifierName();
                }
                name = FinishNode(factory.NewQualifiedName(name, right), pos);
            }
            while (token == SyntaxKind.PrivateIdentifier)
            {
                scanner.ReScanHashToken();
                NextTokenJSDoc();
                name = FinishNode(factory.NewQualifiedName(name, ParseIdentifier()), pos);
            }
            return name;
        }
        return null;
    }

    // jsdoc.go:765
    private (string, bool) ParseJSDocLinkPrefix()
    {
        SkipWhitespaceOrAsterisk();
        if (token == SyntaxKind.OpenBraceToken && NextTokenJSDoc() == SyntaxKind.AtToken && Utilities.TokenIsIdentifierOrKeyword(NextTokenJSDoc()))
        {
            string kind = scanner.TokenValue();
            if (IsJSDocLinkTag(kind))
            {
                return (kind, true);
            }
        }
        return ("NONE", false);
    }

    // jsdoc.go:776
    private static bool IsJSDocLinkTag(string kind) =>
        kind == "link" || kind == "linkcode" || kind == "linkplain";

    // jsdoc.go:780
    private Node ParseUnknownTag(int start, Node? tagName, int indent, string? indentText) =>
        FinishNode(factory.NewJSDocUnknownTag(tagName, ParseTrailingTagComments(start, NodePos(), indent, indentText)), start);

    // jsdoc.go:793
    private (Node, bool) ParseBracketNameInPropertyAndParamTag(PropertyLikeParse target)
    {
        // Looking for something like '[foo]', 'foo', '[foo.bar]' or 'foo.bar'
        bool isBracketed = ParseOptionalJsdoc(SyntaxKind.OpenBracketToken);
        if (isBracketed)
        {
            SkipWhitespace();
        }
        // a markdown-quoted name: `arg` is not legal jsdoc, but occurs in the wild
        bool isBackquoted = ParseOptionalJsdoc(SyntaxKind.BacktickToken);
        Node name = ParseJSDocEntityName(CoreUtil.IfElse<Message?>(target == PropertyLikeParse.Parameter, null, global::Ts.Diagnostics.Diagnostics.Identifier_expected));
        if (isBackquoted)
        {
            ParseExpectedTokenJSDoc(SyntaxKind.BacktickToken);
        }
        if (isBracketed)
        {
            SkipWhitespace();
            // May have an optional default, e.g. '[foo = 42]'
            if (ParseOptionalToken(SyntaxKind.EqualsToken) != null)
            {
                ParseExpression();
            }
            ParseExpected(SyntaxKind.CloseBracketToken);
        }
        return (name, isBracketed);
    }

    // jsdoc.go:833
    private Node ParseParameterOrPropertyTag(int start, Node? tagName, PropertyLikeParse target, int indent)
    {
        Node? typeExpression = TryParseTypeExpression();
        bool isNameFirst = typeExpression == null;
        SkipWhitespaceOrAsterisk();

        (Node name, bool isBracketed) = ParseBracketNameInPropertyAndParamTag(target);
        string indentText = SkipWhitespaceOrAsterisk();

        if (isNameFirst && LookAhead(() => { (_, bool ok) = ParseJSDocLinkPrefix(); return !ok; }))
        {
            typeExpression = TryParseTypeExpression();
        }

        NodeList? comment = ParseTrailingTagComments(start, NodePos(), indent, indentText);

        Node? nestedTypeLiteral = ParseNestedTypeLiteral(typeExpression, name, target, indent);
        if (nestedTypeLiteral != null)
        {
            typeExpression = nestedTypeLiteral;
            isNameFirst = true;
        }
        SyntaxKind kind = CoreUtil.IfElse(target == PropertyLikeParse.Property, SyntaxKind.JSDocPropertyTag, SyntaxKind.JSDocParameterTag);
        Node result = factory.NewJSDocParameterOrPropertyTag(kind, tagName, name, isBracketed, typeExpression, isNameFirst, comment);
        return FinishNode(result, start);
    }

    // jsdoc.go:884
    private Node ParseReturnTag(Node[]? previousTags, int start, Node? tagName, int indent, string? indentText)
    {
        if (CoreUtil.Some(previousTags, NodeTests.IsJSDocReturnTag))
        {
            ParseErrorAt(tagName!.Pos, scanner.TokenStart(), global::Ts.Diagnostics.Diagnostics.X_0_tag_already_specified, tagName.Text());
        }

        Node? typeExpression = TryParseTypeExpression();
        return FinishNode(factory.NewJSDocReturnTag(tagName, typeExpression, ParseTrailingTagComments(start, NodePos(), indent, indentText)), start);
    }

    // pass indent=-1 to skip parsing trailing comments (as when a type tag is nested in a typedef). jsdoc.go:894
    private Node ParseTypeTag(Node[]? previousTags, int start, Node? tagName, int indent, string? indentText)
    {
        if (CoreUtil.Some(previousTags, NodeTests.IsJSDocTypeTag))
        {
            ParseErrorAt(tagName!.Pos, scanner.TokenStart(), global::Ts.Diagnostics.Diagnostics.X_0_tag_already_specified, tagName.Text());
        }

        Node typeExpression = ParseJSDocTypeExpression(true);
        NodeList? comment = null;
        if (indent != -1)
        {
            comment = ParseTrailingTagComments(start, NodePos(), indent, indentText);
        }
        return FinishNode(factory.NewJSDocTypeTag(tagName, typeExpression, comment), start);
    }

    // jsdoc.go:907
    private Node ParseSeeTag(int start, Node? tagName, int indent, string? indentText)
    {
        bool hasNameReference =
            IsIdentifier() && !sourceText.Substring(scanner.TokenEnd()).StartsWith("://", StringComparison.Ordinal) ||
            token == SyntaxKind.OpenBraceToken && LookAhead(NextTokenIsIdentifierOrKeyword);
        Node? nameExpression = null;
        if (hasNameReference)
        {
            nameExpression = ParseJSDocNameReference();
        }
        NodeList? comments = ParseTrailingTagComments(start, NodePos(), indent, indentText);
        return FinishNode(factory.NewJSDocSeeTag(tagName, nameExpression, comments), start);
    }

    // jsdoc.go:918
    private Node ParseImplementsTag(int start, Node? tagName, int margin, string? indentText)
    {
        Node className = ParseExpressionWithTypeArgumentsForAugments();
        return FinishNode(factory.NewJSDocImplementsTag(tagName, className, ParseTrailingTagComments(start, NodePos(), margin, indentText)), start);
    }

    // jsdoc.go:923
    private Node ParseAugmentsTag(int start, Node? tagName, int margin, string? indentText)
    {
        Node className = ParseExpressionWithTypeArgumentsForAugments();
        return FinishNode(factory.NewJSDocAugmentsTag(tagName, className, ParseTrailingTagComments(start, NodePos(), margin, indentText)), start);
    }

    // jsdoc.go:928
    private Node ParseSatisfiesTag(int start, Node? tagName, int margin, string? indentText)
    {
        Node typeExpression = ParseJSDocTypeExpression(false);
        NodeList? comments = ParseTrailingTagComments(start, NodePos(), margin, indentText);
        return FinishNode(factory.NewJSDocSatisfiesTag(tagName, typeExpression, comments), start);
    }

    // jsdoc.go:934
    private Node ParseThrowsTag(int start, Node? tagName, int margin, string? indentText)
    {
        Node? typeExpression = TryParseTypeExpression();
        NodeList? comment = ParseTrailingTagComments(start, NodePos(), margin, indentText);
        return FinishNode(factory.NewJSDocThrowsTag(tagName, typeExpression, comment), start);
    }

    // jsdoc.go:940
    private Node ParseImportTag(int start, Node? tagName, int margin, string? indentText)
    {
        int afterImportTagPos = scanner.TokenFullStart();

        Node? identifier = null;
        if (IsIdentifier())
        {
            identifier = ParseIdentifier();
        }

        Node? importClause = TryParseImportClause(identifier, afterImportTagPos, SyntaxKind.TypeKeyword, true /*skipJSDocLeadingAsterisks*/);
        Node moduleSpecifier = ParseModuleSpecifier();
        Node? attributes = TryParseImportAttributes();

        NodeList? comments = ParseTrailingTagComments(start, NodePos(), margin, indentText);
        return FinishNode(factory.NewJSDocImportTag(tagName, importClause, moduleSpecifier, attributes, comments), start);
    }

    // jsdoc.go:981
    private Node ParseSimpleTag(int start, Func<Node?, NodeList?, Node> createTag, Node? tagName, int margin, string? indentText) =>
        FinishNode(createTag(tagName, ParseTrailingTagComments(start, NodePos(), margin, indentText)), start);

    // jsdoc.go:985
    private Node ParseThisTag(int start, Node? tagName, int margin, string? indentText)
    {
        Node typeExpression = ParseJSDocTypeExpression(true);
        SkipWhitespace();
        Node result = factory.NewJSDocThisTag(tagName, typeExpression, ParseTrailingTagComments(start, NodePos(), margin, indentText));
        return FinishNode(result, start);
    }

    // jsdoc.go:992
    private Node? ParseJSDocTypeNameWithNamespace(bool nested)
    {
        int start = scanner.TokenStart();
        if (!Utilities.TokenIsIdentifierOrKeyword(token))
        {
            return null;
        }
        Node typeNameOrNamespaceName = ParseJSDocIdentifierName(null);
        if (ParseOptionalJsdoc(SyntaxKind.DotToken))
        {
            Node? body = ParseJSDocTypeNameWithNamespace(true /*nested*/);
            Node jsDocNamespaceNode = factory.NewModuleDeclaration(
                null,                      /*modifiers*/
                SyntaxKind.NamespaceKeyword, /*keyword*/
                typeNameOrNamespaceName,
                body);
            if (nested)
            {
                jsDocNamespaceNode.Flags |= NodeFlags.NestedNamespace;
            }
            return FinishNode(jsDocNamespaceNode, start);
        }
        if (nested)
        {
            typeNameOrNamespaceName.Flags |= NodeFlags.IdentifierIsInJSDocNamespace;
        }
        return typeNameOrNamespaceName;
    }

    // jsdoc.go:1017
    private Node ParseTypedefTag(int start, Node? tagName, int indent, string? indentText)
    {
        Node? typeExpression = TryParseTypeExpression();
        SkipWhitespaceOrAsterisk();
        Node? fullName = ParseJSDocTypeNameWithNamespace(false /*nested*/);
        if (fullName == null)
        {
            fullName = ParseJSDocIdentifierName(global::Ts.Diagnostics.Diagnostics.Identifier_expected);
        }
        SkipWhitespace();
        NodeList? comment = ParseTagComments(indent, null);

        int end = -1;
        bool hasChildren = false;
        if (typeExpression == null || IsObjectOrObjectArrayTypeReference(typeExpression.Type()!))
        {
            Node? childTypeTag = null;
            var jsdocPropertyTags = new List<Node>();
            while (true)
            {
                ParserState state = Mark();
                Node? child = ParseChildPropertyTag(indent);
                if (child == null)
                {
                    Rewind(state);
                    break;
                }
                hasChildren = true;
                switch (child.Kind)
                {
                    case SyntaxKind.JSDocTemplateTag:
                        ParseErrorAtRange(child.TagName()!.Loc, global::Ts.Diagnostics.Diagnostics.A_JSDoc_template_tag_may_not_follow_a_typedef_callback_or_overload_tag);
                        break;
                    case SyntaxKind.JSDocTypeTag:
                        if (childTypeTag == null)
                        {
                            childTypeTag = child;
                        }
                        else
                        {
                            Diagnostic? lastError = ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.A_JSDoc_typedef_comment_may_not_contain_multiple_type_tags);
                            if (lastError != null)
                            {
                                Diagnostic related = Diagnostic.NewDiagnostic(null, new TextRange(0, 0), global::Ts.Diagnostics.Diagnostics.The_tag_was_first_specified_here);
                                lastError.AddRelatedInfo(related);
                            }
                        }
                        break;
                    default:
                        jsdocPropertyTags.Add(child);
                        break;
                }
            }
            if (hasChildren)
            {
                bool isArrayType = typeExpression != null && typeExpression.Type()!.Kind == SyntaxKind.ArrayType;
                Node jsdocTypeLiteral = factory.NewJSDocTypeLiteral(jsdocPropertyTags.ToArray(), isArrayType);
                Node? childTypeExpr = childTypeTag?.AsJSDocTypeTag().TypeExpression;
                if (childTypeTag != null && childTypeExpr != null && !IsObjectOrObjectArrayTypeReference(childTypeExpr.Type()!))
                {
                    typeExpression = childTypeExpr;
                }
                else
                {
                    // !!! This differs from Strada but prevents a crash
                    int pos = start;
                    if (jsdocPropertyTags.Count > 0)
                    {
                        pos = jsdocPropertyTags[0].Pos;
                    }
                    typeExpression = FinishNode(jsdocTypeLiteral, pos);
                }
                end = typeExpression.End;
            }
        }

        // Only include the characters between the name end and the next token if a comment was actually
        // parsed out - otherwise it's just whitespace
        if (end == -1)
        {
            if (hasChildren && typeExpression != null)
            {
                end = typeExpression.End;
            }
            else if (comment != null)
            {
                end = NodePos();
            }
            else if (fullName != null)
            {
                end = fullName.End;
            }
            else if (typeExpression != null)
            {
                end = typeExpression.End;
            }
            else
            {
                end = tagName!.End;
            }
        }

        if (comment == null)
        {
            comment = ParseTrailingTagComments(start, end, indent, indentText);
        }

        Node typedefTag = FinishNodeWithEnd(factory.NewJSDocTypedefTag(tagName, typeExpression, fullName, comment), start, end);
        if (typeExpression != null)
        {
            typeExpression.Parent = typedefTag; // forcibly overwrite parent potentially set by inner type expression parse
        }
        return typedefTag;
    }

    // jsdoc.go:1121
    private Node ParseJSDocSignature(int start, int indent)
    {
        NodeList parameters = ParseCallbackTagParameters(indent);
        Node? returnTag = null;
        ParserState state = Mark();
        if (ParseOptionalJsdoc(SyntaxKind.AtToken))
        {
            Node tag = ParseTag(null, indent);
            if (tag.Kind == SyntaxKind.JSDocReturnTag)
            {
                returnTag = tag;
            }
        }
        if (returnTag == null)
        {
            Rewind(state);
        }
        return FinishNode(factory.NewJSDocSignature(null, parameters, returnTag), start);
    }

    // jsdoc.go:1137
    private Node ParseCallbackTag(int start, Node? tagName, int indent, string? indentText)
    {
        Node? fullName = ParseJSDocTypeNameWithNamespace(false /*nested*/);
        if (fullName == null)
        {
            fullName = ParseJSDocIdentifierName(global::Ts.Diagnostics.Diagnostics.Identifier_expected);
        }
        SkipWhitespace();
        NodeList? comment = ParseTagComments(indent, null);
        Node typeExpression = ParseJSDocSignature(NodePos(), indent);
        if (comment == null)
        {
            comment = ParseTrailingTagComments(start, NodePos(), indent, indentText);
        }
        int end = comment != null ? NodePos() : typeExpression.End;
        return FinishNodeWithEnd(factory.NewJSDocCallbackTag(tagName, typeExpression, fullName, comment), start, end);
    }

    // jsdoc.go:1157
    private Node ParseOverloadTag(int start, Node? tagName, int indent, string? indentText)
    {
        SkipWhitespace();
        NodeList? comment = ParseTagComments(indent, null);
        Node typeExpression = ParseJSDocSignature(start, indent);
        if (comment == null)
        {
            comment = ParseTrailingTagComments(start, NodePos(), indent, indentText);
        }
        int end = comment != null ? NodePos() : typeExpression.End;
        return FinishNodeWithEnd(factory.NewJSDocOverloadTag(tagName, typeExpression, comment), start, end);
    }

    // jsdoc.go:1173
    private static bool TextsEqual(Node a, Node b)
    {
        while (!NodeTests.IsIdentifier(a) || !NodeTests.IsIdentifier(b))
        {
            if (!NodeTests.IsIdentifier(a) && !NodeTests.IsIdentifier(b) && a.AsQualifiedName().Right!.Text() == b.AsQualifiedName().Right!.Text())
            {
                a = a.AsQualifiedName().Left!;
                b = b.AsQualifiedName().Left!;
            }
            else
            {
                return false;
            }
        }
        return a.Text() == b.Text();
    }

    // jsdoc.go:1185
    private Node? ParseChildPropertyTag(int indent) =>
        ParseChildParameterOrPropertyTag(PropertyLikeParse.Property, indent, null);

    // jsdoc.go:1189
    private Node? ParseChildParameterOrPropertyTag(PropertyLikeParse target, int indent, Node? name)
    {
        bool canParseTag = true;
        bool seenAsterisk = false;
        while (true)
        {
            switch (NextTokenJSDoc())
            {
                case SyntaxKind.AtToken:
                    if (canParseTag && scanner.CanFollowJSDocAt())
                    {
                        Node? child = TryParseChildTag(target, indent);
                        if (child != null && name != null &&
                            (child.Kind == SyntaxKind.JSDocParameterTag || child.Kind == SyntaxKind.JSDocPropertyTag) &&
                            (NodeTests.IsIdentifier(child.Name()!) || !TextsEqual(name, child.Name()!.AsQualifiedName().Left!)))
                        {
                            return null;
                        }
                        return child;
                    }
                    seenAsterisk = false;
                    break;
                case SyntaxKind.NewLineTrivia:
                    canParseTag = true;
                    seenAsterisk = false;
                    break;
                case SyntaxKind.AsteriskToken:
                    if (seenAsterisk)
                    {
                        canParseTag = false;
                    }
                    seenAsterisk = true;
                    break;
                case SyntaxKind.Identifier:
                    canParseTag = false;
                    break;
                case SyntaxKind.EndOfFile:
                    return null;
            }
        }
    }

    // jsdoc.go:1221
    private Node? TryParseChildTag(PropertyLikeParse target, int indent)
    {
        if (token != SyntaxKind.AtToken)
        {
            throw new InvalidOperationException("should only be called when at @");
        }
        int start = scanner.TokenFullStart();
        NextTokenJSDoc();

        Node tagName = ParseJSDocIdentifierName(global::Ts.Diagnostics.Diagnostics.Identifier_expected);
        string indentText = SkipWhitespaceOrAsterisk();
        PropertyLikeParse t;
        switch (tagName.Text())
        {
            case "type":
                if (target == PropertyLikeParse.Property)
                {
                    return ParseTypeTag(null, start, tagName, -1, "");
                }
                return null;
            case "prop":
            case "property":
                t = PropertyLikeParse.Property;
                break;
            case "arg":
            case "argument":
            case "param":
                t = PropertyLikeParse.Parameter | PropertyLikeParse.CallbackParameter;
                break;
            case "template":
                return ParseTemplateTag(start, tagName, indent, indentText);
            case "this":
                return ParseThisTag(start, tagName, indent, indentText);
            default:
                return null;
        }
        if ((target & t) == 0)
        {
            return null;
        }
        return ParseParameterOrPropertyTag(start, tagName, target, indent);
    }

    // jsdoc.go:1292
    private Node ParseTemplateTag(int start, Node? tagName, int indent, string? indentText)
    {
        Node? constraint = null;
        if (token == SyntaxKind.OpenBraceToken)
        {
            constraint = ParseJSDocTypeExpression(false);
        }
        TypeParameterList typeParameters = ParseTemplateTagTypeParameters();
        Node result = factory.NewJSDocTemplateTag(tagName, constraint, typeParameters, ParseTrailingTagComments(start, NodePos(), indent, indentText));
        return FinishNode(result, start);
    }

    // jsdoc.go:1313
    private bool ParseOptionalJsdoc(SyntaxKind t)
    {
        if (token == t)
        {
            NextTokenJSDoc();
            return true;
        }
        return false;
    }

    // jsdoc.go:1321
    private Node ParseJSDocEntityName(Message? diagnosticMessage)
    {
        Node entity = ParseJSDocIdentifierName(diagnosticMessage);
        if (ParseOptional(SyntaxKind.OpenBracketToken))
        {
            ParseExpected(SyntaxKind.CloseBracketToken);
            // Note that y[] is accepted as an entity name, but the postfix brackets are not saved for checking.
            // Technically usejsdoc.org requires them for specifying a property of a type equivalent to
            // Array<{ x: ...}> but it's not worth it to enforce that restriction.
        }
        while (ParseOptional(SyntaxKind.DotToken))
        {
            Node name = ParseJSDocIdentifierName(global::Ts.Diagnostics.Diagnostics.Identifier_expected);
            if (ParseOptional(SyntaxKind.OpenBracketToken))
            {
                ParseExpected(SyntaxKind.CloseBracketToken);
            }
            int pos = entity.Pos;
            entity = FinishNode(factory.NewQualifiedName(entity, name), pos);
        }
        return entity;
    }

    // jsdoc.go:1340
    private Node ParseJSDocIdentifierName(Message? diagnosticMessage)
    {
        if (!Utilities.TokenIsIdentifierOrKeyword(token))
        {
            if (diagnosticMessage != null)
            {
                ParseErrorAtCurrentToken(diagnosticMessage);
            }
            else if (IsReservedWord(token))
            {
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Identifier_expected_0_is_a_reserved_word_that_cannot_be_used_here, scanner.TokenText());
            }
            return FinishNode(NewIdentifier(""), NodePos());
        }
        int pos = scanner.TokenStart();
        int end = scanner.TokenEnd();
        string text = scanner.TokenValue();
        InternIdentifier(text);
        NextTokenJSDoc();
        return FinishNodeWithEnd(NewIdentifier(text), pos, end);
    }

    // ── parseJSDocType family (parser.go:2844-2878; assigned to the JSDoc cluster) ──

    // parser.go:2844
    private Node ParseJSDocAllType()
    {
        int pos = NodePos();
        NextToken();
        return FinishNode(factory.NewJSDocAllType(), pos);
    }

    // parser.go:2850
    private Node ParseJSDocNonNullableType()
    {
        int pos = NodePos();
        NextToken();
        return FinishNode(factory.NewJSDocNonNullableType(ParseTypeOperatorOrHigher()), pos);
    }

    // parser.go:2856
    private Node ParseJSDocNullableType()
    {
        int pos = NodePos();
        // skip the ?
        NextToken();
        return FinishNode(factory.NewJSDocNullableType(ParseTypeOperatorOrHigher()), pos);
    }

    // parser.go:2863
    private Node ParseJSDocType()
    {
        scanner.SetSkipJSDocLeadingAsterisks(true);
        int pos = NodePos();

        bool hasDotDotDot = ParseOptional(SyntaxKind.DotDotDotToken);
        Node t = ParseTypeOrTypePredicate();
        scanner.SetSkipJSDocLeadingAsterisks(false);
        if (hasDotDotDot)
        {
            t = FinishNode(factory.NewJSDocVariadicType(t), pos);
        }
        if (token == SyntaxKind.EqualsToken)
        {
            NextToken();
            return FinishNode(factory.NewJSDocOptionalType(t), pos);
        }
        return t;
    }

    // ── reparser.go: reparse JS JSDoc into TS-shaped AST ──

    // reparser.go:54 — Hosted tags find a host and add their children under the host.
    // Unhosted tags add synthetic nodes to the reparse list.
    private void ReparseTags(Node? parent, Node[]? jsDoc)
    {
        if (jsDoc == null)
        {
            return;
        }
        foreach (Node j in jsDoc)
        {
            bool isLast = ReferenceEquals(j, jsDoc[^1]);
            NodeList? tags = j.AsJSDoc().Tags;
            if (tags == null)
            {
                continue;
            }
            foreach (Node tag in tags.Nodes)
            {
                ReparseUnhosted(tag, parent, j);
                if (isLast)
                {
                    ReparseHosted(tag, parent, j);
                }
            }
        }
    }

    // reparser.go:70
    private void ReparseUnhosted(Node? tag, Node? parent, Node? jsDoc)
    {
        switch (tag!.Kind)
        {
            case SyntaxKind.JSDocTypedefTag:
                {
                    Node? typeExpression = tag.TypeExpression();
                    if (typeExpression == null)
                    {
                        break;
                    }
                    Node? fullName = tag.Name();
                    bool isNamespace = fullName != null && NodeTests.IsModuleDeclaration(fullName);
                    ModifierList? modifiers = null;
                    if (isNamespace)
                    {
                        modifiers = CreateExportModifier(tag);
                    }
                    Node typeAlias = factory.NewJSTypeAliasDeclaration(modifiers, AddDeepCloneReparse(CheckNonIdentifierName(GetInnermostNameOfJSDocNamespace(fullName))), null, null);
                    typeAlias.AsTypeAliasDeclaration().TypeParameters = GatherTypeParameters(jsDoc, true /*typedefOrCallback*/);
                    Node? t;
                    switch (typeExpression.Kind)
                    {
                        case SyntaxKind.JSDocTypeExpression:
                            t = AddDeepCloneReparse(typeExpression.Type());
                            break;
                        case SyntaxKind.JSDocTypeLiteral:
                            t = ReparseJSDocTypeLiteral(typeExpression);
                            break;
                        default:
                            throw new InvalidOperationException("typedef tag type expression should be a name reference or a type expression" + typeExpression.Kind);
                    }
                    typeAlias.AsTypeAliasDeclaration().Type = t;
                    FinishReparsedNode(typeAlias, tag);
                    jsdocInfos.Add(new JSDocInfo { Parent = typeAlias, JsDocs = new[] { jsDoc! } });
                    typeAlias.Flags |= NodeFlags.HasJSDoc;
                    Node result = WrapInJSDocNamespace(fullName, typeAlias, false /*nested*/);
                    reparseList.Add(result);
                    break;
                }
            case SyntaxKind.JSDocCallbackTag:
                {
                    Node? typeExpression = tag.TypeExpression();
                    if (typeExpression == null)
                    {
                        break;
                    }
                    Node? fullName = tag.Name();
                    bool isNamespace = fullName != null && NodeTests.IsModuleDeclaration(fullName);
                    ModifierList? modifiers = null;
                    if (isNamespace)
                    {
                        modifiers = CreateExportModifier(tag);
                    }
                    Node functionType = ReparseJSDocSignature(typeExpression, tag, jsDoc, tag, null);
                    Node typeAlias = factory.NewJSTypeAliasDeclaration(modifiers, AddDeepCloneReparse(GetInnermostNameOfJSDocNamespace(fullName)), null, functionType);
                    typeAlias.AsTypeAliasDeclaration().TypeParameters = GatherTypeParameters(jsDoc, true /*typedefOrCallback*/);
                    FinishReparsedNode(typeAlias, tag);
                    jsdocInfos.Add(new JSDocInfo { Parent = typeAlias, JsDocs = new[] { jsDoc! } });
                    typeAlias.Flags |= NodeFlags.HasJSDoc;
                    Node result = WrapInJSDocNamespace(fullName, typeAlias, false /*nested*/);
                    reparseList.Add(result);
                    break;
                }
            case SyntaxKind.JSDocImportTag:
                {
                    JSDocImportTag importTag = tag.AsJSDocImportTag();
                    if (importTag.ImportClause == null)
                    {
                        break;
                    }
                    Node importClause = AddDeepCloneReparse(importTag.ImportClause)!;
                    importClause.AsImportClause().PhaseModifier = SyntaxKind.TypeKeyword;
                    Node importDeclaration = factory.NewJSImportDeclaration(
                        factory.DeepCloneReparseModifiers(importTag.Modifiers()),
                        importClause,
                        AddDeepCloneReparse(importTag.ModuleSpecifier),
                        AddDeepCloneReparse(importTag.Attributes));
                    FinishReparsedNode(importDeclaration, tag);
                    reparseList.Add(importDeclaration);
                    break;
                }
            case SyntaxKind.JSDocOverloadTag:
                // Create overload signatures only for function, method, and constructor declarations
                // outside object literals
                if ((NodeTests.IsFunctionDeclaration(parent!) || NodeTests.IsMethodDeclaration(parent!) || NodeTests.IsConstructorDeclaration(parent!)) &&
                    (parsingContexts & (1 << (int)ParsingContext.ObjectLiteralMembers)) == 0)
                {
                    reparseList.Add(ReparseJSDocSignature(tag.AsJSDocOverloadTag().TypeExpression, parent, jsDoc, tag, parent!.Modifiers()));
                }
                break;
        }
    }

    // reparser.go:142
    private Node ReparseJSDocSignature(Node? jsSignature, Node? fun, Node? jsDoc, Node? tag, ModifierList? modifiers)
    {
        Node signature;
        ModifierList? clonedModifiers = factory.DeepCloneReparseModifiers(modifiers);
        switch (fun!.Kind)
        {
            case SyntaxKind.FunctionDeclaration:
                signature = factory.NewFunctionDeclaration(clonedModifiers, null, factory.DeepCloneReparse(CheckNonIdentifierName(fun.Name())), null, null, null, null, null);
                break;
            case SyntaxKind.MethodDeclaration:
                signature = factory.NewMethodDeclaration(clonedModifiers, null, factory.DeepCloneReparse(CheckNonIdentifierName(fun.Name())), null, null, null, null, null, null);
                break;
            case SyntaxKind.Constructor:
                signature = factory.NewConstructorDeclaration(clonedModifiers, null, null, null, null, null);
                break;
            case SyntaxKind.JSDocCallbackTag:
                signature = factory.NewFunctionTypeNode(null, null, factory.NewKeywordTypeNode(SyntaxKind.AnyKeyword));
                break;
            default:
                throw new InvalidOperationException("Unexpected kind " + fun.Kind);
        }

        if (tag!.Kind != SyntaxKind.JSDocCallbackTag)
        {
            signature.FunctionLikeData()!.TypeParameters = GatherTypeParameters(jsDoc, false /*typedefOrCallback*/);
        }
        var parameters = new List<Node>();
        Node[] jsParams = jsSignature!.Parameters();
        for (int pi = 0; pi < jsParams.Length; pi++)
        {
            Node param = jsParams[pi];
            Node? parameter = null;
            if (param.Kind == SyntaxKind.JSDocThisTag)
            {
                JSDocThisTag thisTag = param.AsJSDocThisTag();
                Node thisIdent = factory.NewIdentifier("this");
                thisIdent.Loc = thisTag.Loc;
                thisIdent.Flags = contextFlags | NodeFlags.Reparsed;
                parameter = factory.NewParameterDeclaration(null, null, thisIdent, null, null, null);
                if (thisTag.TypeExpression != null)
                {
                    parameter.AsParameterDeclaration().Type = AddDeepCloneReparse(thisTag.TypeExpression.Type());
                }
            }
            else if (param.Kind == SyntaxKind.JSDocParameterTag || param.Kind == SyntaxKind.JSDocPropertyTag)
            {
                JSDocParameterOrPropertyTag jsparam = param.AsJSDocParameterOrPropertyTag();
                // Skip sub-property parameters (e.g., @param x.y) - these have QualifiedNames and
                // describe properties of a parent parameter, not standalone parameters.
                if (NodeTests.IsQualifiedName(jsparam.Name()!))
                {
                    continue;
                }
                Node? dotDotDotToken = null;
                Node? paramType = null;

                if (jsparam.TypeExpression != null)
                {
                    if (jsparam.TypeExpression.Type()!.Kind == SyntaxKind.JSDocVariadicType)
                    {
                        dotDotDotToken = factory.NewToken(SyntaxKind.DotDotDotToken);
                        dotDotDotToken.Loc = jsparam.Loc;
                        dotDotDotToken.Flags = contextFlags | NodeFlags.Reparsed;

                        JSDocVariadicType variadicType = jsparam.TypeExpression.Type()!.AsJSDocVariadicType();
                        paramType = ReparseJSDocTypeLiteral(variadicType.Type);
                    }
                    else
                    {
                        paramType = ReparseJSDocTypeLiteral(jsparam.TypeExpression.Type());
                    }
                }
                Node? name = jsparam.Name();
                if (NodeTests.IsIdentifier(name!) && !global::Ts.Scanner.Scanner.IsValidIdentifier(name!.AsIdentifier().Text))
                {
                    // drop invalid chars for _, if empty, write _0, etc., so we have a valid param name to emit later
                    // PORT-NOTE: Go ranges over runes; identifier text is ~always BMP so char iteration matches.
                    var result = new StringBuilder();
                    string nameText = name.AsIdentifier().Text;
                    for (int i = 0; i < nameText.Length; i++)
                    {
                        char ch = nameText[i];
                        if (i == 0)
                        {
                            if (!global::Ts.Scanner.Scanner.IsIdentifierStart(ch))
                            {
                                result.Append('_');
                            }
                            else
                            {
                                result.Append(ch);
                            }
                            continue;
                        }
                        else if (!global::Ts.Scanner.Scanner.IsIdentifierPart(ch))
                        {
                            result.Append('_');
                        }
                        else
                        {
                            result.Append(ch);
                        }
                    }
                    if (result.Length == 0)
                    {
                        result.Append('_');
                        result.Append(pi.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    name = AddTransformedReparse(factory.NewIdentifier(result.ToString()), name);
                }
                else
                {
                    name = AddDeepCloneReparse(name);
                }
                parameter = factory.NewParameterDeclaration(null, dotDotDotToken, name, MakeQuestionIfOptional(jsparam), paramType, null);
            }
            FinishReparsedNode(parameter, param);
            parameters.Add(parameter!);
            ReparseJSDocComment(parameter, param);
        }
        signature.FunctionLikeData()!.Parameters = NewNodeList(jsSignature!.AsJSDocSignature().Parameters!.Loc, parameters.ToArray());

        if (jsSignature.Type() != null && jsSignature.Type()!.TypeExpression() != null)
        {
            signature.FunctionLikeData()!.Type = AddDeepCloneReparse(jsSignature.Type()!.TypeExpression()!.Type());
        }
        Node loc = jsSignature;
        if (tag.Kind == SyntaxKind.JSDocOverloadTag)
        {
            loc = tag.TagName()!;
        }
        FinishReparsedNode(signature, loc);
        return signature;
    }

    // reparser.go:240
    private Node? ReparseJSDocTypeLiteral(Node? t)
    {
        if (t == null)
        {
            return null;
        }
        if (t.Kind == SyntaxKind.JSDocTypeLiteral)
        {
            JSDocTypeLiteral jstypeliteral = t.AsJSDocTypeLiteral();
            bool isArrayType = jstypeliteral.IsArrayType;
            var properties = new List<Node>();
            foreach (Node prop in jstypeliteral.JSDocPropertyTags ?? Array.Empty<Node>())
            {
                if (prop.Kind != SyntaxKind.JSDocPropertyTag && prop.Kind != SyntaxKind.JSDocParameterTag)
                {
                    continue;
                }
                JSDocParameterOrPropertyTag jsprop = prop.AsJSDocParameterOrPropertyTag();
                Node? name = prop.Name();
                if (name!.Kind == SyntaxKind.QualifiedName)
                {
                    name = name.AsQualifiedName().Right;
                }
                if (NodeTests.IsIdentifier(name!) && !global::Ts.Scanner.Scanner.IsValidIdentifier(name!.AsIdentifier().Text))
                {
                    name = AddTransformedReparse(factory.NewStringLiteral(name.AsIdentifier().Text, TokenFlags.None), name);
                }
                else
                {
                    name = AddDeepCloneReparse(name);
                }
                Node property = factory.NewPropertySignatureDeclaration(null, name, MakeQuestionIfOptional(jsprop), null, null);
                if (jsprop.TypeExpression != null)
                {
                    property.AsPropertySignatureDeclaration().Type = ReparseJSDocTypeLiteral(jsprop.TypeExpression.Type());
                }
                FinishReparsedNode(property, prop);
                properties.Add(property);
                ReparseJSDocComment(property, prop);
            }
            Node literal = factory.NewTypeLiteralNode(NewNodeList(jstypeliteral.Loc, properties.ToArray()));
            if (isArrayType)
            {
                FinishReparsedNode(literal, jstypeliteral);
                literal = factory.NewArrayTypeNode(literal);
            }
            FinishReparsedNode(literal, jstypeliteral);
            return literal;
        }
        return AddDeepCloneReparse(t);
    }

    // reparser.go:281
    private void ReparseJSDocComment(Node? node, Node? tag)
    {
        NodeList? comment = tag!.CommentList();
        if (comment != null)
        {
            Node[] clones = new Node[comment.Nodes.Length];
            for (int i = 0; i < comment.Nodes.Length; i++)
            {
                clones[i] = factory.DeepCloneReparse(comment.Nodes[i])!;
            }
            NodeList newComment = factory.NewNodeList(clones);
            newComment.Loc = comment.Loc;
            Node propJSDoc = factory.NewJSDoc(newComment, null);
            FinishReparsedNode(propJSDoc, tag);
            propJSDoc.Parent = node;
            jsdocInfos.Add(new JSDocInfo { Parent = node!, JsDocs = new[] { propJSDoc } });
            node!.Flags |= NodeFlags.HasJSDoc;
        }
    }

    // reparser.go:342
    private void ReparseHosted(Node? tag, Node? parent, Node? jsDoc)
    {
        switch (tag!.Kind)
        {
            case SyntaxKind.JSDocTypeTag:
                switch (parent!.Kind)
                {
                    case SyntaxKind.VariableStatement:
                        if (parent.AsVariableStatement().DeclarationList != null)
                        {
                            foreach (Node declaration in parent.AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes)
                            {
                                if (declaration.Type() == null && tag.TypeExpression() != null)
                                {
                                    declaration.AsMutable().SetType(AddDeepCloneReparse(tag.TypeExpression()!.Type()));
                                    FinishMutatedNode(declaration);
                                    return;
                                }
                            }
                        }
                        break;
                    case SyntaxKind.VariableDeclaration:
                    case SyntaxKind.ExportAssignment:
                    case SyntaxKind.PropertyDeclaration:
                    case SyntaxKind.PropertyAssignment:
                    case SyntaxKind.ShorthandPropertyAssignment:
                    case SyntaxKind.GetAccessor:
                        if (parent.Type() == null && tag.TypeExpression() != null)
                        {
                            parent.AsMutable().SetType(AddDeepCloneReparse(tag.TypeExpression()!.Type()));
                            FinishMutatedNode(parent);
                            return;
                        }
                        break;
                    case SyntaxKind.Parameter:
                        if (parent.Type() == null && tag.TypeExpression() != null)
                        {
                            parent.AsMutable().SetType(ReparseJSDocTypeLiteral(tag.TypeExpression()!.Type()));
                            FinishMutatedNode(parent);
                            return;
                        }
                        break;
                    case SyntaxKind.ExpressionStatement:
                        // reparser.go:369 — JS assignment-declaration (module.exports = …, exports.x = …,
                        // C.prototype = …) @type reparse: attach the annotation to the BinaryExpression.
                        if (parent.Expression()!.Kind == SyntaxKind.BinaryExpression)
                        {
                            var bin = parent.Expression()!;
                            var kind = AstUtilities.GetAssignmentDeclarationKind(bin);
                            if (kind != JSDeclarationKind.None && tag.TypeExpression() != null)
                            {
                                bin.AsMutable().SetType(AddDeepCloneReparse(tag.TypeExpression()!.Type()));
                                FinishMutatedNode(bin);
                                return;
                            }
                        }
                        break;
                    case SyntaxKind.ReturnStatement:
                    case SyntaxKind.ParenthesizedExpression:
                        if (parent.Expression() != null && tag.TypeExpression() != null)
                        {
                            parent.AsMutable().SetExpression(MakeNewCast(
                                AddDeepCloneReparse(tag.TypeExpression()!.Type()),
                                parent.Expression(),
                                true /*isAssertion*/));
                            FinishMutatedNode(parent);
                            return;
                        }
                        break;
                }
                {
                    Node? fun = GetFunctionLikeHost(parent);
                    if (fun != null)
                    {
                        bool noTypedParams = CoreUtil.Every(fun.Parameters(), static p => p.Type() == null);
                        if (fun.TypeParameterList() == null && fun.Type() == null && noTypedParams && tag.TypeExpression() != null)
                        {
                            fun.FunctionLikeData()!.FullSignature = AddDeepCloneReparse(tag.TypeExpression()!.Type());
                            FinishMutatedNode(fun);
                        }
                    }
                }
                break;
            case SyntaxKind.JSDocSatisfiesTag:
                switch (parent!.Kind)
                {
                    case SyntaxKind.VariableStatement:
                        if (parent.AsVariableStatement().DeclarationList != null)
                        {
                            foreach (Node declaration in parent.AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes)
                            {
                                if (declaration.Initializer() != null && tag.TypeExpression() != null)
                                {
                                    declaration.AsMutable().SetInitializer(MakeNewCast(
                                        AddDeepCloneReparse(tag.TypeExpression()!.Type()),
                                        declaration.Initializer(),
                                        false /*isAssertion*/));
                                    FinishMutatedNode(declaration);
                                    break;
                                }
                            }
                        }
                        break;
                    case SyntaxKind.VariableDeclaration:
                    case SyntaxKind.PropertyDeclaration:
                    case SyntaxKind.PropertyAssignment:
                        if (parent.Initializer() != null && tag.TypeExpression() != null)
                        {
                            parent.AsMutable().SetInitializer(MakeNewCast(
                                AddDeepCloneReparse(tag.TypeExpression()!.Type()),
                                parent.Initializer(),
                                false /*isAssertion*/));
                            FinishMutatedNode(parent);
                        }
                        break;
                    case SyntaxKind.ShorthandPropertyAssignment:
                        {
                            ShorthandPropertyAssignment shorthand = parent.AsShorthandPropertyAssignment();
                            if (shorthand.ObjectAssignmentInitializer != null && tag.AsJSDocSatisfiesTag().TypeExpression != null)
                            {
                                shorthand.ObjectAssignmentInitializer = MakeNewCast(
                                    AddDeepCloneReparse(tag.AsJSDocSatisfiesTag().TypeExpression!.Type()),
                                    shorthand.ObjectAssignmentInitializer,
                                    false /*isAssertion*/);
                                FinishMutatedNode(parent);
                            }
                            break;
                        }
                    case SyntaxKind.ReturnStatement:
                    case SyntaxKind.ParenthesizedExpression:
                    case SyntaxKind.ExportAssignment:
                        if (parent.Expression() != null && tag.TypeExpression() != null)
                        {
                            parent.AsMutable().SetExpression(MakeNewCast(
                                AddDeepCloneReparse(tag.TypeExpression()!.Type()),
                                parent.Expression(),
                                false /*isAssertion*/));
                            FinishMutatedNode(parent);
                        }
                        break;
                    case SyntaxKind.ExpressionStatement:
                        // reparser.go:440-451 — JS assignment-declaration @satisfies reparse: the
                        // synthesized satisfies-expression wraps the BinaryExpression's right-hand side.
                        if (parent.Expression()!.Kind == SyntaxKind.BinaryExpression)
                        {
                            var bin = parent.Expression()!.AsBinaryExpression();
                            var kind = AstUtilities.GetAssignmentDeclarationKind(bin);
                            if (kind != JSDeclarationKind.None && tag.TypeExpression() != null)
                            {
                                bin.Right = MakeNewCast(
                                    AddDeepCloneReparse(tag.TypeExpression()!.Type()),
                                    bin.Right,
                                    false /*isAssertion*/);
                                FinishMutatedNode(bin);
                            }
                        }
                        break;
                }
                break;
            case SyntaxKind.JSDocTemplateTag:
                {
                    Node? fun = GetFunctionLikeHost(parent);
                    if (fun != null)
                    {
                        if (fun.TypeParameters() == null && fun.FunctionLikeData()!.FullSignature == null)
                        {
                            fun.FunctionLikeData()!.TypeParameters = GatherTypeParameters(jsDoc, false /*typedefOrCallback*/);
                            FinishMutatedNode(fun);
                        }
                    }
                    else if (parent!.Kind == SyntaxKind.ClassDeclaration)
                    {
                        ClassDeclaration @class = parent.AsClassDeclaration();
                        if (@class.TypeParameters == null)
                        {
                            @class.TypeParameters = GatherTypeParameters(jsDoc, false /*typedefOrCallback*/);
                            FinishMutatedNode(parent);
                        }
                    }
                    else if (parent.Kind == SyntaxKind.ClassExpression)
                    {
                        ClassExpression @class = parent.AsClassExpression();
                        if (@class.TypeParameters == null)
                        {
                            @class.TypeParameters = GatherTypeParameters(jsDoc, false /*typedefOrCallback*/);
                            FinishMutatedNode(parent);
                        }
                    }
                    break;
                }
            case SyntaxKind.JSDocParameterTag:
                {
                    Node? fun = GetFunctionLikeHost(parent);
                    if (fun != null && fun.FunctionLikeData()!.FullSignature == null)
                    {
                        JSDocParameterOrPropertyTag parameterTag = tag.AsJSDocParameterOrPropertyTag();
                        (ParameterDeclaration? param, bool ok) = FindMatchingParameter(fun, parameterTag, jsDoc);
                        if (ok)
                        {
                            if (param!.Type == null && parameterTag.TypeExpression != null)
                            {
                                param.Type = ReparseJSDocTypeLiteral(parameterTag.TypeExpression.Type());
                            }
                            if (param.QuestionToken == null)
                            {
                                Node? question = MakeQuestionIfOptional(parameterTag);
                                if (question != null)
                                {
                                    param.QuestionToken = question;
                                }
                            }
                            FinishMutatedNode(param);
                        }
                    }
                    break;
                }
            case SyntaxKind.JSDocThisTag:
                {
                    Node? fun = GetFunctionLikeHost(parent);
                    if (fun != null)
                    {
                        Node[] @params = fun.Parameters();
                        if (@params.Length == 0 || (@params[0].Name()!.Kind != SyntaxKind.ThisKeyword && !global::Ts.Ast.JSDocReparseAstGaps.IsThisIdentifier(@params[0].Name())))
                        {
                            Node thisParam = factory.NewParameterDeclaration(
                                null, /* decorators */
                                null, /* modifiers */
                                factory.NewIdentifier("this"),
                                null, /* questionToken */
                                null, /* type */
                                null  /* initializer */);
                            if (tag.AsJSDocThisTag().TypeExpression != null)
                            {
                                thisParam.AsParameterDeclaration().Type = AddDeepCloneReparse(tag.AsJSDocThisTag().TypeExpression!.Type());
                            }
                            FinishReparsedNode(thisParam, tag.TagName());

                            Node[] newParams = new Node[@params.Length + 1];
                            newParams[0] = thisParam;
                            for (int i = 0; i < @params.Length; i++)
                            {
                                newParams[i + 1] = @params[i];
                            }

                            fun.FunctionLikeData()!.Parameters = NewNodeList(fun.FunctionLikeData()!.Parameters!.Loc, newParams);
                            FinishMutatedNode(fun);
                        }
                    }
                    break;
                }
            case SyntaxKind.JSDocReturnTag:
                {
                    Node? fun = GetFunctionLikeHost(parent);
                    if (fun != null && fun.FunctionLikeData()!.FullSignature == null)
                    {
                        if (fun.Type() == null && tag.TypeExpression() != null)
                        {
                            fun.FunctionLikeData()!.Type = AddDeepCloneReparse(tag.TypeExpression()!.Type());
                            FinishMutatedNode(fun);
                        }
                    }
                    break;
                }
            case SyntaxKind.JSDocReadonlyTag:
            case SyntaxKind.JSDocPrivateTag:
            case SyntaxKind.JSDocPublicTag:
            case SyntaxKind.JSDocProtectedTag:
            case SyntaxKind.JSDocOverrideTag:
                {
                    if (parent!.Kind == SyntaxKind.ExpressionStatement)
                    {
                        parent = parent.Expression();
                    }
                    switch (parent!.Kind)
                    {
                        case SyntaxKind.MethodDeclaration:
                        case SyntaxKind.GetAccessor:
                        case SyntaxKind.SetAccessor:
                            // In object literals these aren't class-like members, so JSDoc modifiers like
                            // @override or @readonly aren't real modifiers there; reparsing them produces
                            // spurious grammar errors (#4437).
                            if ((parsingContexts & (1 << (int)ParsingContext.ObjectLiteralMembers)) != 0)
                            {
                                return;
                            }
                            goto case SyntaxKind.PropertyDeclaration;
                        case SyntaxKind.PropertyDeclaration:
                        case SyntaxKind.Constructor:
                        case SyntaxKind.BinaryExpression:
                            {
                                SyntaxKind keyword = tag.Kind switch
                                {
                                    SyntaxKind.JSDocReadonlyTag => SyntaxKind.ReadonlyKeyword,
                                    SyntaxKind.JSDocPrivateTag => SyntaxKind.PrivateKeyword,
                                    SyntaxKind.JSDocPublicTag => SyntaxKind.PublicKeyword,
                                    SyntaxKind.JSDocProtectedTag => SyntaxKind.ProtectedKeyword,
                                    SyntaxKind.JSDocOverrideTag => SyntaxKind.OverrideKeyword,
                                    _ => SyntaxKind.Unknown,
                                };
                                Node modifier = factory.NewModifier(keyword);
                                modifier.Loc = tag.Loc;
                                modifier.Flags = contextFlags | NodeFlags.Reparsed;
                                Node[] nodes;
                                TextRange loc;
                                if (parent.Modifiers() == null)
                                {
                                    nodes = new[] { modifier };
                                    loc = tag.Loc;
                                }
                                else
                                {
                                    Node[] existing = parent.ModifierNodes()!;
                                    nodes = new Node[existing.Length + 1];
                                    Array.Copy(existing, nodes, existing.Length);
                                    nodes[existing.Length] = modifier;
                                    loc = parent.Modifiers()!.Loc;
                                }
                                parent.AsMutable().SetModifiers(NewModifierList(loc, nodes));
                                FinishMutatedNode(parent);
                                break;
                            }
                    }
                    break;
                }
            case SyntaxKind.JSDocImplementsTag:
                {
                    ClassLikeBase? @class = GetClassLikeData(parent);
                    if (@class != null)
                    {
                        JSDocImplementsTag implementsTag = tag.AsJSDocImplementsTag();

                        if (@class.HeritageClauses != null)
                        {
                            Node? implementsClause = CoreUtil.Find(@class.HeritageClauses.Nodes, static node => node.AsHeritageClause().Token == SyntaxKind.ImplementsKeyword);
                            if (implementsClause != null)
                            {
                                implementsClause.AsHeritageClause().Types!.Nodes = AppendNode(implementsClause.AsHeritageClause().Types!.Nodes, AddDeepCloneReparse(implementsTag.ClassName)!);
                                FinishMutatedNode(implementsClause);
                                return;
                            }
                        }
                        NodeList typesList = NewNodeList(implementsTag.ClassName!.Loc, new[] { AddDeepCloneReparse(implementsTag.ClassName)! });

                        Node heritageClause = factory.NewHeritageClause(SyntaxKind.ImplementsKeyword, typesList);
                        FinishReparsedNode(heritageClause, implementsTag.ClassName);

                        if (@class.HeritageClauses == null)
                        {
                            NodeList heritageClauses = NewNodeList(implementsTag.ClassName!.Loc, new[] { heritageClause });
                            @class.HeritageClauses = heritageClauses;
                        }
                        else
                        {
                            @class.HeritageClauses.Nodes = AppendNode(@class.HeritageClauses.Nodes, heritageClause);
                        }
                        FinishMutatedNode(parent);
                    }
                    break;
                }
            case SyntaxKind.JSDocAugmentsTag:
                {
                    ClassLikeBase? @class = GetClassLikeData(parent);
                    if (@class != null && @class.HeritageClauses != null)
                    {
                        Node? extendsClause = CoreUtil.Find(@class.HeritageClauses.Nodes, static node => node.AsHeritageClause().Token == SyntaxKind.ExtendsKeyword);
                        if (extendsClause != null && extendsClause.AsHeritageClause().Types!.Nodes.Length == 1)
                        {
                            ExpressionWithTypeArguments target = extendsClause.AsHeritageClause().Types!.Nodes[0].AsExpressionWithTypeArguments();
                            ExpressionWithTypeArguments source = tag.ClassName()!.AsExpressionWithTypeArguments();
                            if (global::Ts.Ast.JSDocReparseAstGaps.HasSamePropertyAccessName(target.Expression!, source.Expression!))
                            {
                                if (target.TypeArguments == null && source.TypeArguments != null)
                                {
                                    Node[] newArguments = new Node[source.TypeArguments.Nodes.Length];
                                    for (int i = 0; i < source.TypeArguments.Nodes.Length; i++)
                                    {
                                        newArguments[i] = AddDeepCloneReparse(source.TypeArguments.Nodes[i])!;
                                    }
                                    target.TypeArguments = NewNodeList(source.TypeArguments.Loc, newArguments);
                                    FinishMutatedNode(target);
                                }
                            }
                        }
                    }
                    break;
                }
        }
    }

    // reparser.go:621
    private static (ParameterDeclaration?, bool) FindMatchingParameter(Node fun, JSDocParameterOrPropertyTag parameterTag, Node? jsDoc)
    {
        int tagIndex = -1;
        int paramCount = -1;
        foreach (Node tag in jsDoc!.AsJSDoc().Tags!.Nodes)
        {
            if (tag.Kind == SyntaxKind.JSDocParameterTag)
            {
                paramCount++;
                if (ReferenceEquals(tag.AsJSDocParameterOrPropertyTag(), parameterTag))
                {
                    tagIndex = paramCount;
                    break;
                }
            }
        }
        Node[] parameters = fun.Parameters();
        for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
        {
            Node parameter = parameters[parameterIndex];
            if (parameter.Name()!.Kind == SyntaxKind.Identifier)
            {
                if (parameterTag.Name()!.Kind == SyntaxKind.Identifier &&
                    (parameter.Name()!.Text() == parameterTag.Name()!.Text() || (parameterIndex == tagIndex && parameterTag.Name()!.Text().Length == 0)))
                {
                    return (parameter.AsParameterDeclaration(), true);
                }
            }
            else if (parameterIndex == tagIndex)
            {
                return (parameter.AsParameterDeclaration(), true);
            }
        }
        return (null, false);
    }

    // reparser.go:646
    private static Node? SkipSatisfiesExpressions(Node? node)
    {
        while (node != null && node.Kind == SyntaxKind.SatisfiesExpression)
        {
            node = node.Expression();
        }
        return node;
    }

    // reparser.go:653
    private static Node? GetFunctionLikeHost(Node? host)
    {
        Node? fun = host;
        switch (host!.Kind)
        {
            case SyntaxKind.VariableStatement:
                {
                    Node[] nodes = host.AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes;
                    if (nodes.Length != 0)
                    {
                        fun = nodes[0].Initializer();
                    }
                    break;
                }
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.PropertyDeclaration:
                fun = host.Initializer();
                break;
            case SyntaxKind.ExportAssignment:
            case SyntaxKind.ReturnStatement:
                fun = host.Expression();
                break;
            case SyntaxKind.ExpressionStatement:
                fun = AstUtilities.GetRightMostAssignedExpression(host.Expression()!);
                break;
        }
        fun = SkipSatisfiesExpressions(fun);
        if (AstUtilities.IsFunctionLike(fun))
        {
            return fun;
        }
        return null;
    }

    // reparser.go:685
    private static ClassLikeBase? GetClassLikeData(Node? parent) => parent!.ClassLikeData();

    // reparser.go:704
    private Node? GetInnermostNameOfJSDocNamespace(Node? fullName)
    {
        if (fullName == null)
        {
            return null;
        }
        while (fullName!.Kind == SyntaxKind.ModuleDeclaration)
        {
            Node? body = fullName.AsModuleDeclaration().Body;
            if (body == null)
            {
                return fullName.Name();
            }
            fullName = body;
        }
        return fullName;
    }

    // reparser.go:729
    private Node WrapInJSDocNamespace(Node? fullName, Node? statement, bool nested)
    {
        if (fullName == null || !NodeTests.IsModuleDeclaration(fullName))
        {
            return statement!;
        }
        // Recursively wrap from outermost to innermost. Inner namespaces always get an export modifier so
        // members are accessible via dotted access from outside. The outermost namespace is treated as
        // exported only in module files via IsImplicitlyExportedJSDocDeclaration (in the binder), so it does
        // not get an explicit export modifier here.
        Node wrapped = WrapInJSDocNamespace(fullName.AsModuleDeclaration().Body, statement, true /*nested*/);
        Node block = factory.NewModuleBlock(NewNodeList(fullName.Loc, new[] { wrapped }));
        FinishReparsedNode(block, fullName);
        ModifierList? modifiers = null;
        if (nested)
        {
            modifiers = CreateExportModifier(fullName);
        }
        Node result = factory.NewModuleDeclaration(modifiers, SyntaxKind.NamespaceKeyword, AddDeepCloneReparse(fullName.Name()), block);
        FinishReparsedNode(result, fullName);
        reparsedClones.Add(result);
        return result;
    }

    // parser.go:518
    private Node ReparseTopLevelAwait(SourceFile? sourceFile)
    {
        if (possibleAwaitSpans.Count % 2 == 1)
        {
            throw new InvalidOperationException("possibleAwaitSpans malformed: odd number of indices, not paired into spans.");
        }
        var statements = new List<Node>();
        Diagnostic[] savedParseDiagnostics = diagnostics.ToArray();
        diagnostics.Clear();

        Node[] sourceStatements = sourceFile!.Statements!.Nodes;
        int afterAwaitStatement = 0;
        for (int i = 0; i < possibleAwaitSpans.Count; i += 2)
        {
            int nextAwaitStatement = possibleAwaitSpans[i];
            // append all non-await statements between afterAwaitStatement and nextAwaitStatement
            Node prevStatement = sourceStatements[afterAwaitStatement];
            Node nextStatement = sourceStatements[nextAwaitStatement];
            for (int s = afterAwaitStatement; s < nextAwaitStatement; s++)
            {
                statements.Add(sourceStatements[s]);
            }

            // append all diagnostics associated with the copied range
            int diagnosticStart = CoreUtil.FindIndex(savedParseDiagnostics, d => d.Pos() >= prevStatement.Pos);
            int diagnosticEnd;
            if (diagnosticStart >= 0)
            {
                Diagnostic[] rest = savedParseDiagnostics[diagnosticStart..];
                diagnosticEnd = CoreUtil.FindIndex(rest, d => d.Pos() >= nextStatement.Pos);
            }
            else
            {
                diagnosticEnd = -1;
            }
            if (diagnosticStart >= 0)
            {
                if (diagnosticEnd >= 0)
                {
                    for (int d = diagnosticStart; d < diagnosticStart + diagnosticEnd; d++)
                    {
                        diagnostics.Add(savedParseDiagnostics[d]);
                    }
                }
                else
                {
                    for (int d = diagnosticStart; d < savedParseDiagnostics.Length; d++)
                    {
                        diagnostics.Add(savedParseDiagnostics[d]);
                    }
                }
            }

            ParserState state = Mark();
            // reparse all statements between start and pos. We skip existing diagnostics for the same range
            // and allow the parser to generate new ones.
            contextFlags |= NodeFlags.AwaitContext;
            scanner.ResetPos(nextStatement.Pos);
            NextToken();

            afterAwaitStatement = possibleAwaitSpans[i + 1];
            while (token != SyntaxKind.EndOfFile)
            {
                int startPos = scanner.TokenFullStart();
                Node statement = ParseStatement();
                statements.Add(statement);
                if (startPos == scanner.TokenFullStart())
                {
                    NextToken();
                }
                if (afterAwaitStatement < sourceStatements.Length)
                {
                    Node lastAwaitStatement = sourceStatements[afterAwaitStatement - 1];
                    if (statement.End == lastAwaitStatement.End)
                    {
                        // done reparsing this section
                        break;
                    }
                    if (statement.End > lastAwaitStatement.End)
                    {
                        // we ate into the next statement, so we must continue reparsing the next span
                        i += 2;
                        if (i < possibleAwaitSpans.Count)
                        {
                            afterAwaitStatement = possibleAwaitSpans[i + 1];
                        }
                        else
                        {
                            afterAwaitStatement = sourceStatements.Length;
                        }
                    }
                }
            }

            // Keep diagnostics from the reparse
            state.DiagnosticsLen = diagnostics.Count;
            Rewind(state);
        }

        // append all statements between pos and the end of the list
        if (afterAwaitStatement < sourceStatements.Length)
        {
            Node prevStatement = sourceStatements[afterAwaitStatement];
            for (int s = afterAwaitStatement; s < sourceStatements.Length; s++)
            {
                statements.Add(sourceStatements[s]);
            }

            // append all diagnostics associated with the copied range
            int diagnosticStart = CoreUtil.FindIndex(savedParseDiagnostics, d => d.Pos() >= prevStatement.Pos);
            if (diagnosticStart >= 0)
            {
                for (int d = diagnosticStart; d < savedParseDiagnostics.Length; d++)
                {
                    diagnostics.Add(savedParseDiagnostics[d]);
                }
            }
        }

        Node result = factory.NewSourceFile(sourceFile.ParseOptions(), sourceText, NewNodeList(sourceFile.Statements!.Loc, statements.ToArray()), sourceFile.EndOfFileToken);
        foreach (Node s in statements)
        {
            s.Parent = result; // force (re)set parent to reparsed source file
        }
        return result;
    }

    private static Node[] AppendNode(Node[] arr, Node n)
    {
        Node[] result = new Node[arr.Length + 1];
        Array.Copy(arr, result, arr.Length);
        result[arr.Length] = n;
        return result;
    }

    // ── lazy JSDoc parsing hook (jsdoc.go:13-37 init/parseJSDocForNode) ──

    // Registered into the AST layer so ast.Node.JSDoc(file) can lazily parse JSDoc for a node in a
    // TS file on first access. Mirrors Go's package init() calling ast.SetParseJSDocForNode. Runs as
    // part of Parser's static initialization, which is triggered before any ParseSourceFile call —
    // hence before any lazy-JSDoc SourceFile can exist. (A static field initializer is used instead of
    // [ModuleInitializer] to satisfy analyzer CA2255.)
    private static readonly bool jsdocHookRegistered = RegisterJSDocParseHook();

    private static bool RegisterJSDocParseHook()
    {
        global::Ts.Ast.JSDocLazyResolve.ParseForNode = ParseJSDocForNode;
        return true;
    }

    // jsdoc.go:19 — parseJSDocForNode lazily parses JSDoc for a node in a TS file.
    internal static Node[]? ParseJSDocForNode(SourceFile sourceFile, Node node)
    {
        Parser p = GetParser();
        try
        {
            p.InitializeState(sourceFile.ParseOptions(), sourceFile.Text(), sourceFile.ScriptKind);
            List<CommentRange> ranges = Utilities.GetJSDocCommentRanges(p.factory, new List<CommentRange>(), node, sourceFile.Text());
            if (ranges.Count == 0)
            {
                return null;
            }
            var jsdoc = new List<Node>(ranges.Count);
            int pos = node.Pos;
            foreach (CommentRange comment in ranges)
            {
                Node? parsed = p.ParseJSDocComment(node, comment.Pos, comment.End, pos);
                if (parsed != null)
                {
                    parsed.Parent = node;
                    jsdoc.Add(parsed);
                    pos = parsed.End;
                }
            }
            return jsdoc.ToArray();
        }
        finally
        {
            PutParser(p);
        }
    }
}
