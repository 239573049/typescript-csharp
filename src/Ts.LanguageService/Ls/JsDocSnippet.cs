// Ported from tsgo internal/ls/jsdoc_snippet.go (whole file except the LanguageService entry
// pair, which lives in Completions.cs): the position probe, the doc-comment template generator
// and the template→snippet transforms.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Ls;

internal static class JsDocSnippet
{
    // jsdoc_snippet.go:74-85
    internal static bool IsPotentiallyValidJSDocSnippetCompletionPosition(SourceFile file, int position)
    {
        var text = file.Text();
        var lineStart = global::Ts.Format.Util.GetLineStartPositionForPosition(position, file);
        var prefix = text[lineStart..position];
        if (!IsJSDocSnippetPrefix(prefix))
        {
            return false;
        }

        var lineEnd = LsDeps.GetLineEndOfPosition(file, position);
        var suffix = text[position..lineEnd];
        return IsJSDocSnippetSuffix(suffix);
    }

    // jsdoc_snippet.go:497-515 — isJSDocSnippetPrefix.
    internal static bool IsJSDocSnippetPrefix(string prefix)
    {
        var trimmed = TrimRightSingleLineWhitespace(prefix);
        if (trimmed.EndsWith("/**", System.StringComparison.Ordinal))
        {
            return true;
        }
        var start = SkipSingleLineWhitespace(prefix, 0);
        if (start >= trimmed.Length || trimmed[start] != '/')
        {
            return false;
        }
        if (start + 3 > trimmed.Length)
        {
            return false;
        }
        for (var i = start + 1; i < trimmed.Length; i++)
        {
            if (trimmed[i] != '*')
            {
                return false;
            }
        }
        return trimmed.Length - start >= 3;
    }

    // jsdoc_snippet.go:517-528 — getJSDocSnippetPrefixStart.
    internal static (int Start, bool Ok) GetJSDocSnippetPrefixStart(string prefix)
    {
        var trimmed = TrimRightSingleLineWhitespace(prefix);
        for (var i = trimmed.Length - 1; i >= 0 && trimmed[i] == '*'; i--)
        {
            if (i > 0 && trimmed[i - 1] == '/')
            {
                return (i - 1, true);
            }
        }
        if (trimmed.EndsWith('/'))
        {
            return (trimmed.Length - 1, true);
        }
        return (0, false);
    }

    // jsdoc_snippet.go:530-544 — isJSDocSnippetSuffix.
    internal static bool IsJSDocSnippetSuffix(string suffix)
    {
        var trimmed = TrimRightSingleLineWhitespace(suffix[SkipSingleLineWhitespace(suffix, 0)..]);
        if (trimmed.Length == 0)
        {
            return true;
        }
        if (!trimmed.EndsWith('/'))
        {
            return false;
        }
        for (var i = 0; i < trimmed.Length - 1; i++)
        {
            if (trimmed[i] != '*')
            {
                return false;
            }
        }
        return true;
    }

    // jsdoc_snippet.go:537-546 — getJSDocSnippetSuffixEnd.
    internal static (int End, bool Ok) GetJSDocSnippetSuffixEnd(string suffix)
    {
        var pos = SkipSingleLineWhitespace(suffix, 0);
        while (pos < suffix.Length && suffix[pos] == '*')
        {
            pos++;
        }
        if (pos < suffix.Length && suffix[pos] == '/')
        {
            return (pos + 1, true);
        }
        return (0, false);
    }

    // jsdoc_snippet.go:549-562 — trimRightSingleLineWhitespace. Go walks UTF-8 bytes; C# walks
    // UTF-16 units with surrogate pairing — equivalent per code point.
    internal static string TrimRightSingleLineWhitespace(string text)
    {
        var end = 0;
        for (var pos = 0; pos < text.Length;)
        {
            int ch;
            int size;
            if (char.IsHighSurrogate(text[pos]) && pos + 1 < text.Length && char.IsLowSurrogate(text[pos + 1]))
            {
                ch = char.ConvertToUtf32(text[pos], text[pos + 1]);
                size = 2;
            }
            else
            {
                ch = text[pos];
                size = 1;
            }
            pos += size;
            if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(ch))
            {
                end = pos;
            }
        }
        return text[..end];
    }

    // jsdoc_snippet.go:564-573 — skipSingleLineWhitespace.
    internal static int SkipSingleLineWhitespace(string text, int pos)
    {
        while (pos < text.Length)
        {
            int ch;
            int size;
            if (char.IsHighSurrogate(text[pos]) && pos + 1 < text.Length && char.IsLowSurrogate(text[pos + 1]))
            {
                ch = char.ConvertToUtf32(text[pos], text[pos + 1]);
                size = 2;
            }
            else
            {
                ch = text[pos];
                size = 1;
            }
            if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(ch))
            {
                break;
            }
            pos += size;
        }
        return pos;
    }

    // jsdoc_snippet.go:121-176 — getDocCommentTemplateAtPosition.
    internal static DocCommentTemplate? GetDocCommentTemplateAtPosition(SourceFile sourceFile, int position, bool generateReturnInDocTemplate, string newLine)
    {
        var tokenAtPos = global::Ts.AstNav.Tokens.GetTokenAtPosition(sourceFile, position);
        if (tokenAtPos == null)
        {
            return null;
        }

        var existingDocComment = AstUtilities.FindAncestor(tokenAtPos, NodeTests.IsJSDoc);
        var (docCommentEnd, hasDocCommentAtPosition, hasClosingDocCommentAtPosition) = GetDocCommentEndAtPosition(sourceFile, position);
        var isInEmptyDocComment = existingDocComment != null || hasDocCommentAtPosition;
        if (IsNonEmptyJSDoc(existingDocComment) && hasDocCommentAtPosition && !hasClosingDocCommentAtPosition)
        {
            var reparseText = sourceFile.Text()[..position] + " */" + sourceFile.Text()[position..];
            var reparse = global::Ts.Parser.Parser.ParseSourceFile(sourceFile.ParseOptions(), reparseText, sourceFile.ScriptKind);
            return GetDocCommentTemplateAtPosition(reparse, position, generateReturnInDocTemplate, newLine);
        }
        if (IsNonEmptyJSDoc(existingDocComment))
        {
            return null;
        }
        if (existingDocComment == null && hasDocCommentAtPosition)
        {
            tokenAtPos = global::Ts.AstNav.Tokens.GetTokenAtPosition(sourceFile, SkipWhitespace(sourceFile.Text(), docCommentEnd));
            if (tokenAtPos == null)
            {
                return null;
            }
        }
        var tokenStart = global::Ts.AstNav.Tokens.GetStartOfNode(tokenAtPos, sourceFile, false /*includeJSDoc*/);
        if (!isInEmptyDocComment && tokenStart < position)
        {
            return null;
        }

        var commentOwnerInfo = GetCommentOwnerInfo(tokenAtPos, generateReturnInDocTemplate);
        if (commentOwnerInfo == null)
        {
            return null;
        }

        var commentOwner = commentOwnerInfo.commentOwner;
        var lastJSDoc = CoreUtil.LastOrNil(commentOwner.JSDoc(sourceFile));
        var commentOwnerStart = global::Ts.AstNav.Tokens.GetStartOfNode(commentOwner, sourceFile, false /*includeJSDoc*/);
        if (commentOwnerStart < position ||
            lastJSDoc != null && existingDocComment != null && lastJSDoc != existingDocComment)
        {
            return null;
        }

        var indentation = GetIndentationStringAtPosition(sourceFile, position);
        var tags = ParameterDocComments(commentOwnerInfo.parameters ?? [], AstUtilities.IsSourceFileJS(sourceFile), indentation, newLine);
        if (commentOwnerInfo.hasReturn)
        {
            tags += ReturnsDocComment(indentation, newLine);
        }

        if (tags.Length != 0 && !HasJSDocTags(commentOwner, sourceFile))
        {
            var preamble = "/**" + newLine + indentation + " * ";
            var endLine = "";
            if (tokenStart == position)
            {
                endLine = newLine + indentation;
            }
            return new DocCommentTemplate { newText = preamble + newLine + tags + indentation + " */" + endLine };
        }
        return new DocCommentTemplate { newText = "/** */" };
    }

    // jsdoc_snippet.go:178-189 — getDocCommentEndAtPosition.
    internal static (int End, bool Ok, bool HasClosing) GetDocCommentEndAtPosition(SourceFile file, int position)
    {
        var text = file.Text();
        var lineStart = global::Ts.Format.Util.GetLineStartPositionForPosition(position, file);
        var lineEnd = LsDeps.GetLineEndOfPosition(file, position);
        var prefix = text[lineStart..position];
        var suffix = text[position..lineEnd];
        if (!TrimRightSingleLineWhitespace(prefix).EndsWith("/**", StringComparison.Ordinal))
        {
            return (0, false, false);
        }
        var (suffixEnd, hasClosing) = GetJSDocSnippetSuffixEnd(suffix);
        return (position + suffixEnd, true, hasClosing);
    }

    // jsdoc_snippet.go:191-203 — skipWhitespace. (Go walks UTF-8 runes; whitespace is BMP-only,
    // so a UTF-16 unit walk is equivalent.)
    internal static int SkipWhitespace(string text, int position)
    {
        while (position < text.Length)
        {
            if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceLike(text[position]))
            {
                break;
            }
            position++;
        }
        return position;
    }

    // jsdoc_snippet.go:205-213 — getCommentOwnerInfo.
    internal static CommentOwnerInfo? GetCommentOwnerInfo(Node tokenAtPos, bool generateReturnInDocTemplate)
    {
        for (var node = (Node?)tokenAtPos; node != null; node = node.Parent)
        {
            var (info, quit) = GetCommentOwnerInfoWorker(node, generateReturnInDocTemplate);
            if (info != null || quit)
            {
                return info;
            }
        }
        return null;
    }

    // jsdoc_snippet.go:215-265 — getCommentOwnerInfoWorker.
    internal static (CommentOwnerInfo? Info, bool Quit) GetCommentOwnerInfoWorker(Node? commentOwner, bool generateReturnInDocTemplate)
    {
        if (commentOwner == null)
        {
            return (null, false);
        }
        switch (commentOwner.Kind)
        {
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.Constructor:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.ArrowFunction:
                return (new CommentOwnerInfo { commentOwner = commentOwner, parameters = commentOwner.Parameters(), hasReturn = HasReturn(commentOwner, generateReturnInDocTemplate) }, false);
            case SyntaxKind.PropertyAssignment:
                return GetCommentOwnerInfoWorker(commentOwner.AsPropertyAssignment().Initializer, generateReturnInDocTemplate);
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.EnumMember:
            case SyntaxKind.TypeAliasDeclaration:
                return (new CommentOwnerInfo { commentOwner = commentOwner }, false);
            case SyntaxKind.PropertySignature:
            {
                var typeNode = commentOwner.AsPropertySignatureDeclaration().Type;
                if (typeNode != null && NodeTests.IsFunctionTypeNode(typeNode))
                {
                    return (new CommentOwnerInfo { commentOwner = commentOwner, parameters = typeNode.Parameters(), hasReturn = HasReturn(typeNode, generateReturnInDocTemplate) }, false);
                }
                return (new CommentOwnerInfo { commentOwner = commentOwner }, false);
            }
            case SyntaxKind.VariableStatement:
            {
                var declarations = commentOwner.AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes;
                if (declarations.Length == 1)
                {
                    var initializer = declarations[0].AsVariableDeclaration().Initializer;
                    if (initializer != null)
                    {
                        var host = GetRightHandSideOfAssignment(initializer);
                        if (host != null)
                        {
                            return (new CommentOwnerInfo { commentOwner = commentOwner, parameters = host.Parameters(), hasReturn = HasReturn(host, generateReturnInDocTemplate) }, false);
                        }
                    }
                }
                return (new CommentOwnerInfo { commentOwner = commentOwner }, false);
            }
            case SyntaxKind.SourceFile:
                return (null, true);
            case SyntaxKind.ModuleDeclaration:
                if (commentOwner.Parent!.Kind == SyntaxKind.ModuleDeclaration)
                {
                    return (null, false);
                }
                return (new CommentOwnerInfo { commentOwner = commentOwner }, false);
            case SyntaxKind.ExpressionStatement:
                return GetCommentOwnerInfoWorker(commentOwner.AsExpressionStatement().Expression, generateReturnInDocTemplate);
            case SyntaxKind.BinaryExpression:
            {
                var binaryExpression = commentOwner.AsBinaryExpression();
                if (AstUtilities.GetAssignmentDeclarationKind(commentOwner) == JSDeclarationKind.None)
                {
                    return (null, true);
                }
                if (AstUtilities.IsFunctionLike(binaryExpression.Right!))
                {
                    return (new CommentOwnerInfo { commentOwner = commentOwner, parameters = binaryExpression.Right!.Parameters(), hasReturn = HasReturn(binaryExpression.Right!, generateReturnInDocTemplate) }, false);
                }
                return (new CommentOwnerInfo { commentOwner = commentOwner }, false);
            }
            case SyntaxKind.PropertyDeclaration:
            {
                var initializer = commentOwner.AsPropertyDeclaration().Initializer;
                if (initializer != null && AstUtilities.IsFunctionExpressionOrArrowFunction(initializer))
                {
                    return (new CommentOwnerInfo { commentOwner = commentOwner, parameters = initializer.Parameters(), hasReturn = HasReturn(initializer, generateReturnInDocTemplate) }, false);
                }
                break;
            }
        }
        return (null, false);
    }

    // jsdoc_snippet.go:267-282 — hasReturn.
    internal static bool HasReturn(Node node, bool generateReturnInDocTemplate)
    {
        if (!generateReturnInDocTemplate)
        {
            return false;
        }
        if (NodeTests.IsFunctionTypeNode(node))
        {
            return true;
        }
        if (NodeTests.IsArrowFunction(node))
        {
            var arrowBody = node.Body();
            if (arrowBody != null && AstUtilities.IsExpression(arrowBody))
            {
                return true;
            }
        }
        return AstUtilities.IsFunctionLikeDeclaration(node) && node.Body() != null && NodeTests.IsBlock(node.Body()!) && AstUtilities.ForEachReturnStatement(node.Body()!, _ => true);
    }

    // jsdoc_snippet.go:284-299 — getRightHandSideOfAssignment.
    internal static Node? GetRightHandSideOfAssignment(Node? rightHandSide)
    {
        if (rightHandSide == null)
        {
            return null;
        }
        while (rightHandSide.Kind == SyntaxKind.ParenthesizedExpression)
        {
            rightHandSide = rightHandSide.AsParenthesizedExpression().Expression!;
        }
        switch (rightHandSide.Kind)
        {
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
                return rightHandSide;
            case SyntaxKind.ClassExpression:
                return CoreUtil.Find(rightHandSide.Members(), NodeTests.IsConstructorDeclaration);
            default:
                return null;
        }
    }

    // jsdoc_snippet.go:301-323 — parameterDocComments.
    internal static string ParameterDocComments(Node[] parameters, bool isJavaScriptFile, string indentation, string newLine)
    {
        var b = new System.Text.StringBuilder();
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var paramName = $"param{i}";
            if (NodeTests.IsIdentifier(parameter.Name()!))
            {
                paramName = parameter.Name()!.Text();
            }
            var paramType = "";
            if (isJavaScriptFile)
            {
                if (parameter.AsParameterDeclaration().DotDotDotToken != null)
                {
                    paramType = "{...any} ";
                }
                else
                {
                    paramType = "{any} ";
                }
            }
            b.Append(indentation);
            b.Append(" * @param ");
            b.Append(paramType);
            b.Append(paramName);
            b.Append(newLine);
        }
        return b.ToString();
    }

    // jsdoc_snippet.go:325-327 — returnsDocComment.
    internal static string ReturnsDocComment(string indentation, string newLine)
    {
        return indentation + " * @returns" + newLine;
    }

    // jsdoc_snippet.go:329-344 — getIndentationStringAtPosition.
    internal static string GetIndentationStringAtPosition(SourceFile sourceFile, int position)
    {
        var text = sourceFile.Text();
        var lineStart = global::Ts.Format.Util.GetLineStartPositionForPosition(position, sourceFile);
        var pos = lineStart;
        while (pos < position)
        {
            if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(text[pos]))
            {
                break;
            }
            pos++;
        }
        return text[lineStart..pos];
    }

    // jsdoc_snippet.go:346-352 — isNonEmptyJSDoc.
    internal static bool IsNonEmptyJSDoc(Node? jsdoc)
    {
        if (jsdoc == null)
        {
            return false;
        }
        var data = jsdoc.AsJSDoc();
        return data.Comment != null && data.Comment.Nodes.Length > 0 || data.Tags != null && data.Tags.Nodes.Length > 0;
    }

    // jsdoc_snippet.go:354-361 — hasJSDocTags.
    internal static bool HasJSDocTags(Node node, SourceFile file)
    {
        var jsdocs = node.JSDoc(file) ?? [];
        if (jsdocs.Length == 0)
        {
            return false;
        }
        var tags = jsdocs[^1].AsJSDoc().Tags;
        return tags != null && tags.Nodes.Length > 0;
    }

    // jsdoc_snippet.go:363-372 — templateToSnippet.
    internal static string TemplateToSnippet(string template, string newLine)
    {
        if (template == "/** */")
        {
            return "/**" + newLine + " * $0" + newLine + " */";
        }

        var snippetIndex = 1;
        template = LanguageService.EscapeSnippetText(template);
        template = StripJSDocTemplateIndentation(template, newLine);
        return TransformJSDocTemplateLines(template, newLine, ref snippetIndex);
    }

    // jsdoc_snippet.go:374-385 — stripJSDocTemplateIndentation.
    internal static string StripJSDocTemplateIndentation(string template, string newLine)
    {
        var lines = template.Split(newLine);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart(' ', '\t');
            if (trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                lines[i] = trimmed;
            }
            else if (trimmed.StartsWith("*", StringComparison.Ordinal))
            {
                lines[i] = " " + trimmed;
            }
        }
        return string.Join(newLine, lines);
    }

    // jsdoc_snippet.go:387-403 — transformJSDocTemplateLines.
    internal static string TransformJSDocTemplateLines(string template, string newLine, ref int snippetIndex)
    {
        var lines = template.Split(newLine);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (i > 0 && lines[i - 1].StartsWith("/**", StringComparison.Ordinal) && LineHasOnlyJSDocAsterisk(line))
            {
                lines[i] = line + "$0";
                continue;
            }
            var (transformedParam, okParam) = TransformJSDocParamLine(line, ref snippetIndex);
            if (okParam)
            {
                lines[i] = transformedParam;
                continue;
            }
            var (transformedReturns, okReturns) = TransformJSDocReturnsLine(line, ref snippetIndex);
            if (okReturns)
            {
                lines[i] = transformedReturns;
            }
        }
        return string.Join(newLine, lines);
    }

    // jsdoc_snippet.go:405-408 — lineHasOnlyJSDocAsterisk.
    internal static bool LineHasOnlyJSDocAsterisk(string line)
    {
        line = line.TrimStart(' ', '\t');
        return line.StartsWith("*", StringComparison.Ordinal) && IsOnlySpacesOrTabs(line[1..]);
    }

    // jsdoc_snippet.go:410-455 — transformJSDocParamLine.
    internal static (string Text, bool Ok) TransformJSDocParamLine(string line, ref int snippetIndex)
    {
        var prefix = "";
        var rest = line;
        if (rest.StartsWith(" ", StringComparison.Ordinal))
        {
            prefix = " ";
            rest = rest[1..];
        }
        if (!rest.StartsWith("* @param", StringComparison.Ordinal))
        {
            return ("", false);
        }
        rest = rest["* @param".Length..];
        if (!StartsWithSingleLineWhitespace(rest))
        {
            return ("", false);
        }
        rest = rest.TrimStart(' ', '\t');

        var typeText = "";
        if (rest.StartsWith("{", StringComparison.Ordinal))
        {
            var closeBrace = rest.IndexOf('}');
            if (closeBrace < 0)
            {
                return ("", false);
            }
            typeText = " " + rest[..(closeBrace + 1)];
            rest = rest[(closeBrace + 1)..];
            if (!StartsWithSingleLineWhitespace(rest))
            {
                return ("", false);
            }
            rest = rest.TrimStart(' ', '\t');
        }

        var (paramName, remaining, ok) = ScanNonWhitespace(rest);
        if (!ok || !IsOnlySpacesOrTabs(remaining))
        {
            return ("", false);
        }

        var outText = prefix + "* @param ";
        if (typeText == " {any}" || typeText == " {*}")
        {
            outText += "{${" + snippetIndex + ":*}} ";
            snippetIndex++;
        }
        else if (typeText.Length != 0)
        {
            outText += typeText + " ";
        }
        outText += paramName + " ${" + snippetIndex + "}";
        snippetIndex++;
        return (outText, true);
    }

    // jsdoc_snippet.go:457-470 — transformJSDocReturnsLine.
    internal static (string Text, bool Ok) TransformJSDocReturnsLine(string line, ref int snippetIndex)
    {
        var prefix = "";
        var rest = line;
        if (rest.StartsWith(" ", StringComparison.Ordinal))
        {
            prefix = " ";
            rest = rest[1..];
        }
        if (!rest.StartsWith("* @returns", StringComparison.Ordinal) || !IsOnlySpacesOrTabs(rest["* @returns".Length..]))
        {
            return ("", false);
        }
        var text = prefix + "* @returns ${" + snippetIndex + "}";
        snippetIndex++;
        return (text, true);
    }

    // jsdoc_snippet.go:472-487 — scanNonWhitespace.
    internal static (string Word, string Remaining, bool Ok) ScanNonWhitespace(string text)
    {
        if (text.Length == 0)
        {
            return ("", "", false);
        }
        for (var i = 0; i < text.Length; i++)
        {
            if (global::Ts.StringUtil.StringUtil.IsWhiteSpaceLike(text[i]))
            {
                if (i == 0)
                {
                    return ("", "", false);
                }
                return (text[..i], text[i..], true);
            }
        }
        return (text, "", true);
    }

    // jsdoc_snippet.go:575-577 — isOnlySingleLineWhitespace.
    internal static bool IsOnlySingleLineWhitespace(string text)
    {
        return SkipSingleLineWhitespace(text, 0) == text.Length;
    }

    // jsdoc_snippet.go:579-585 — startsWithSingleLineWhitespace.
    internal static bool StartsWithSingleLineWhitespace(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }
        return global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(text[0]);
    }

    // jsdoc_snippet.go:587-594 — isOnlySpacesOrTabs.
    internal static bool IsOnlySpacesOrTabs(string text)
    {
        foreach (var c in text)
        {
            if (c != ' ' && c != '\t')
            {
                return false;
            }
        }
        return true;
    }
}

// jsdoc_snippet.go:19-21 — docCommentTemplate.
internal sealed class DocCommentTemplate
{
    internal string newText = "";
}

// jsdoc_snippet.go:23-27 — commentOwnerInfo.
internal sealed class CommentOwnerInfo
{
    internal Node commentOwner = null!;
    internal Node[]? parameters;
    internal bool hasReturn;
}
