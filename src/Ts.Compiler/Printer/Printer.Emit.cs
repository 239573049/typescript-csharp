// Ported from tsgo internal/printer/printer.go (Top-level nodes + Lists + General/Write dispatch +
// Comments + Source Maps + Name Generation + Scoped operations sections).

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Core;

namespace Ts.Printer;

using EF = global::Ts.Printer.EmitFlags;
using AstUtil = global::Ts.Ast.AstUtilities;
using Scanner = global::Ts.Scanner.Scanner;
using SourceMapSource = global::Ts.SourceMap.Source;

public sealed partial class Printer
{
    //
    // Top-level nodes
    //

    private void EmitShebangIfNeeded(SourceFile node)
    {
        if (AstUtil.NodeIsSynthesized(node.AsNode()))
        {
            return;
        }
        var shebang = Scanner.GetShebang(node.Text());
        if (shebang != "")
        {
            WriteComment(shebang);
            WriteLine();
        }
    }

    private int EmitPrologueDirectives(StatementList statements)
    {
        for (int i = 0; i < statements.Nodes.Length; i++)
        {
            var statement = statements.Nodes[i];
            if (AstUtil.IsPrologueDirective(statement))
            {
                WriteLine();
                EmitStatement(statement);
            }
            else
            {
                return i;
            }
        }
        return statements.Nodes.Length;
    }

    private bool EmitHelpers(Node node)
    {
        bool helpersEmitted = false;
        var sourceFile = currentSourceFile;
        bool shouldSkip = Options.NoEmitHelpers || (sourceFile is not null && emitContext.HasRecordedExternalHelpers(sourceFile));
        var helpersSource = emitContext.GetEmitHelpers(node);
        var helpers = helpersSource is null ? System.Array.Empty<EmitHelper>() : (EmitHelper[])helpersSource.Clone();
        if (helpers.Length > 0)
        {
            // Go: slices.SortStableFunc(helpers, compareEmitHelpers). Array.Sort is unstable; use a stable
            // OrderBy (05-conventions.md §6.4, R22) keyed by CompareEmitHelpers.
            helpers = helpers.OrderBy(h => h, Comparer<EmitHelper>.Create(Helpers.CompareEmitHelpers)).ToArray();
            foreach (var helper in helpers)
            {
                if (!helper.Scoped)
                {
                    if (shouldSkip)
                    {
                        continue;
                    }
                }
                if (helper.TextCallback is not null)
                {
                    WriteLines(helper.TextCallback(makeFileLevelOptimisticUniqueName));
                }
                else
                {
                    WriteLines(helper.Text);
                }
                helpersEmitted = true;
            }
        }

        return helpersEmitted;
    }

    private void EmitSourceFileWorker(SourceFile node)
    {
        var savedCurrentSourceFile = currentSourceFile;
        var savedCommentsDisabled = commentsDisabled;
        currentSourceFile = node;

        WriteLine();

        PushNameGenerationScope(node.AsNode());
        GenerateAllNames(node.Statements);

        int index = 0;
        CommentState? state;
        if (node.ScriptKind != ScriptKind.Json)
        {
            EmitShebangIfNeeded(node);
            index = EmitPrologueDirectives(node.Statements!);
            if (!writer.IsAtStartOfLine())
            {
                WriteLine();
            }
            state = EmitDetachedCommentsBeforeStatementList(node.AsNode(), node.Statements!.Loc);
            EmitHelpers(node.AsNode());
            if (node.IsDeclarationFile)
            {
                EmitTripleSlashDirectives(node);
            }
        }
        else
        {
            state = EmitDetachedCommentsBeforeStatementList(node.AsNode(), node.Statements!.Loc);
        }

        EmitListRange(
            static (p, n) => p.EmitStatement(n),
            node.AsNode(),
            node.Statements,
            ListFormat.MultiLine,
            index,
            -1 /*count*/);
        PopNameGenerationScope(node.AsNode());
        EmitDetachedCommentsAfterStatementList(node.AsNode(), node.Statements.Loc, state);
        currentSourceFile = savedCurrentSourceFile;
        commentsDisabled = savedCommentsDisabled;
    }

    private void EmitTripleSlashDirectives(SourceFile node)
    {
        EmitDirective("path", node.ReferencedFiles);
        EmitDirective("types", node.TypeReferenceDirectives);
        EmitDirective("lib", node.LibReferenceDirectives);
    }

    private void EmitDirective(string kind, FileReference[]? refs)
    {
        foreach (var reference in refs ?? System.Array.Empty<FileReference>())
        {
            string resolutionMode = "";
            if (reference.ResolutionMode != global::Ts.Core.Core.ResolutionModeNone)
            {
                resolutionMode = $"resolution-mode=\"{(reference.ResolutionMode == global::Ts.Core.Core.ResolutionModeESM ? "import" : "require")}\" ";
            }
            WriteComment($"/// <reference {kind}=\"{reference.FileName}\" {resolutionMode}{(reference.Preserve ? "preserve=\"true\" " : "")}/>");
            WriteLine();
        }
    }

    //
    // Lists
    //

    private void EmitList(Action<Printer, Node> emit, Node? parentNode, NodeList? children, ListFormat format)
    {
        if (parentNode is not null && ShouldEmitOnMultipleLines(parentNode))
        {
            format |= ListFormat.PreferNewLine | ListFormat.Indented;
        }

        EmitListRange(emit, parentNode, children, format, -1 /*start*/, -1 /*count*/);
    }

    private void EmitListRange(Action<Printer, Node> emit, Node? parentNode, NodeList? children, ListFormat format, int start, int count)
    {
        bool isNil = children is null;

        int length = 0;
        if (!isNil)
        {
            length = children!.Nodes.Length;
        }

        if (start < 0)
        {
            start = 0;
        }

        if (count < 0)
        {
            count = length - start;
        }

        if (isNil && (format & ListFormat.OptionalIfNil) != 0)
        {
            return;
        }

        bool isEmpty = isNil || start >= length || count <= 0;
        if (isEmpty && (format & ListFormat.OptionalIfEmpty) != 0)
        {
            OnBeforeEmitNodeList?.Invoke(children);
            OnAfterEmitNodeList?.Invoke(children);
            return;
        }

        if ((format & ListFormat.BracketsMask) != 0)
        {
            WritePunctuation(GetOpeningBracket(format));
            if (isEmpty && !isNil)
            {
                EmitTrailingComments(children!.Pos, CommentSeparator.Before); // Emit comments within empty lists
            }
        }

        OnBeforeEmitNodeList?.Invoke(children);

        if (isEmpty)
        {
            // Write a line terminator if the parent node was multi-line
            if ((format & ListFormat.MultiLine) != 0 && !(Options.PreserveSourceNewlines && (parentNode is null || currentSourceFile is not null && PrinterUtilities.RangeIsOnSingleLine(parentNode.Loc, currentSourceFile))))
            {
                WriteLine();
            }
            else if ((format & ListFormat.SpaceBetweenBraces) != 0 && (format & ListFormat.NoSpaceIfEmpty) == 0)
            {
                WriteSpace();
            }
        }
        else
        {
            int end = Math.Min(start + count, length);

            EmitListItems(emit, parentNode, children!.Nodes[start..end], format, HasTrailingComma(parentNode!, children), children.Loc);
        }

        OnAfterEmitNodeList?.Invoke(children);

        if ((format & ListFormat.BracketsMask) != 0)
        {
            if (isEmpty && !isNil)
            {
                EmitLeadingComments(children!.End, false /*elided*/); // Emit comments within empty lists
            }
            WritePunctuation(GetClosingBracket(format));
        }
    }

    private bool HasTrailingComma(Node parentNode, NodeList children)
    {
        if (!children.HasTrailingComma())
        {
            return false;
        }

        var originalParent = emitContext.MostOriginal(parentNode)!;
        if (originalParent == parentNode)
        {
            return true;
        }

        if (originalParent.Kind != parentNode.Kind)
        {
            return false;
        }

        NodeList? originalList = children;
        switch (originalParent.Kind)
        {
            case SyntaxKind.ObjectLiteralExpression:
                originalList = originalParent.PropertyList();
                break;
            case SyntaxKind.ArrayLiteralExpression:
                originalList = originalParent.ElementList();
                break;
            case SyntaxKind.CallExpression:
            case SyntaxKind.NewExpression:
                if (children == parentNode.TypeArgumentList())
                {
                    originalList = originalParent.TypeArgumentList();
                }
                else if (children == parentNode.ArgumentList())
                {
                    originalList = originalParent.ArgumentList();
                }
                break;
            case SyntaxKind.Constructor:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.FunctionType:
            case SyntaxKind.ConstructorType:
            case SyntaxKind.CallSignature:
            case SyntaxKind.ConstructSignature:
                if (children == parentNode.TypeParameterList())
                {
                    originalList = originalParent.TypeParameterList();
                }
                else if (children == parentNode.ParameterList())
                {
                    originalList = originalParent.ParameterList();
                }
                break;
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                if (children == parentNode.TypeParameterList())
                {
                    originalList = originalParent.TypeParameterList();
                }
                break;
            case SyntaxKind.ObjectBindingPattern:
            case SyntaxKind.ArrayBindingPattern:
                if (children == parentNode.ElementList())
                {
                    originalList = originalParent.ElementList();
                }
                break;
            case SyntaxKind.NamedImports:
            case SyntaxKind.NamedExports:
                originalList = originalParent.ElementList();
                break;
            case SyntaxKind.ImportAttributes:
                originalList = originalParent.AsImportAttributes().Attributes;
                break;
        }

        if (originalList is not null)
        {
            return originalList.HasTrailingComma();
        }

        return false;
    }

    private void WriteDelimiter(ListFormat format)
    {
        switch (format & ListFormat.DelimitersMask)
        {
            case ListFormat.None:
                break;
            case ListFormat.CommaDelimited:
                WritePunctuation(",");
                break;
            case ListFormat.BarDelimited:
                WriteSpace();
                WritePunctuation("|");
                break;
            case ListFormat.AsteriskDelimited:
                WriteSpace();
                WritePunctuation("*");
                WriteSpace();
                break;
            case ListFormat.AmpersandDelimited:
                WriteSpace();
                WritePunctuation("&");
                break;
        }
    }

    private void EmitListItems(
        Action<Printer, Node> emit,
        Node? parentNode,
        Node[] children,
        ListFormat format,
        bool hasTrailingComma,
        TextRange childrenTextRange)
    {
        bool mayEmitInterveningComments = (format & ListFormat.NoInterveningComments) == 0;
        bool shouldEmitInterveningComments = mayEmitInterveningComments;

        int leadingLineTerminatorCount = 0;
        if (children.Length > 0)
        {
            leadingLineTerminatorCount = GetLeadingLineTerminatorCount(parentNode, children[0], format);
        }
        if (leadingLineTerminatorCount > 0)
        {
            for (int i = 0; i < leadingLineTerminatorCount; i++)
            {
                WriteLine();
            }
            shouldEmitInterveningComments = false;
        }
        else if ((format & ListFormat.SpaceBetweenBraces) != 0)
        {
            WriteSpace();
        }

        if ((format & ListFormat.Indented) != 0)
        {
            IncreaseIndent();
        }

        int parentEnd = PrinterUtilities.GreatestEnd(-1, parentNode);

        Node? previousSibling = null;
        bool shouldDecreaseIndentAfterEmit = false;
        foreach (var child in children)
        {
            if ((format & ListFormat.AsteriskDelimited) != 0)
            {
                // always write JSDoc in the format "\n *"
                WriteLine();
                WriteDelimiter(format);
            }
            else if (previousSibling is not null)
            {
                if ((format & ListFormat.DelimitersMask) != 0 && previousSibling.End != parentEnd)
                {
                    if (!commentsDisabled && ShouldEmitTrailingComments(previousSibling))
                    {
                        EmitLeadingComments(previousSibling.End, false /*elided*/);
                    }
                }

                WriteDelimiter(format);

                int separatingLineTerminatorCount = GetSeparatingLineTerminatorCount(previousSibling, child, format);
                if (separatingLineTerminatorCount > 0)
                {
                    if ((format & (ListFormat.LinesMask | ListFormat.Indented)) == ListFormat.SingleLine)
                    {
                        IncreaseIndent();
                        shouldDecreaseIndentAfterEmit = true;
                    }

                    if (shouldEmitInterveningComments && (format & ListFormat.DelimitersMask) != 0 && !AstUtil.PositionIsSynthesized(child.Pos) && ShouldEmitLeadingComments(child))
                    {
                        var commentRange = emitContext.CommentRange(child);
                        EmitTrailingCommentsOfPosition(commentRange.Pos, (format & ListFormat.SpaceBetweenSiblings) != 0, true /*forceNoNewline*/);
                    }

                    for (int i = 0; i < separatingLineTerminatorCount; i++)
                    {
                        WriteLine();
                    }

                    shouldEmitInterveningComments = false;
                }
                else if ((format & ListFormat.SpaceBetweenSiblings) != 0)
                {
                    WriteSpace();
                }
            }

            if (shouldEmitInterveningComments && ShouldEmitLeadingComments(child))
            {
                var commentRange = emitContext.CommentRange(child);
                EmitTrailingCommentsOfPosition(commentRange.Pos, false /*prefixSpace*/, false /*forceNoNewline*/);
            }
            else
            {
                shouldEmitInterveningComments = mayEmitInterveningComments;
            }

            nextListElementPos = child.Pos;
            emit(this, child);

            if (shouldDecreaseIndentAfterEmit)
            {
                DecreaseIndent();
                shouldDecreaseIndentAfterEmit = false;
            }

            previousSibling = child;
        }

        bool skipTrailingComments = commentsDisabled || previousSibling is null || !ShouldEmitTrailingComments(previousSibling);
        bool emitTrailingComma = hasTrailingComma && (format & ListFormat.AllowTrailingComma) != 0 && (format & ListFormat.CommaDelimited) != 0;
        if (emitTrailingComma)
        {
            if (previousSibling is not null && !skipTrailingComments)
            {
                EmitToken(SyntaxKind.CommaToken, previousSibling.End, WriteKind.Punctuation, previousSibling);
            }
            else
            {
                WritePunctuation(",");
            }
        }

        if (previousSibling is not null && parentEnd != previousSibling.End && (format & ListFormat.DelimitersMask) != 0 && !skipTrailingComments)
        {
            int commentsPos;
            if (emitTrailingComma && childrenTextRange.End > 0)
            {
                commentsPos = childrenTextRange.End;
            }
            else
            {
                commentsPos = previousSibling.End;
            }
            EmitLeadingComments(commentsPos, false /*elided*/);
        }

        if ((format & ListFormat.Indented) != 0)
        {
            DecreaseIndent();
        }

        int closingLineTerminatorCount = GetClosingLineTerminatorCount(parentNode, global::Ts.Core.CoreUtil.LastOrNil(children), format, childrenTextRange);
        if (closingLineTerminatorCount > 0)
        {
            for (int i = 0; i < closingLineTerminatorCount; i++)
            {
                WriteLine();
            }
        }
        else if ((format & (ListFormat.SpaceAfterList | ListFormat.SpaceBetweenBraces)) != 0)
        {
            WriteSpace();
        }
    }

    //
    // General
    //

    public string Emit(Node node, SourceFile? sourceFile)
    {
        // ensure a reusable writer
        ownWriter ??= TextWriter.NewTextWriter(Options.NewLine.GetNewLineCharacter(), 0);

        Write(node, sourceFile, ownWriter, null /*sourceMapGenerator*/);
        var text = ownWriter.String();

        ownWriter.Clear();
        return text;
    }

    public string EmitSourceFile(SourceFile sourceFile)
    {
        return Emit(sourceFile.AsNode(), sourceFile);
    }

    private void SetSourceFile(SourceFile? sourceFile)
    {
        currentSourceFile = sourceFile;
        uniqueHelperNames = null;
        externalHelpersModuleName = null;
        if (sourceFile is not null)
        {
            if ((emitContext.EmitFlags(emitContext.MostOriginal(sourceFile.AsNode())!) & EF.ExternalHelpers) != 0)
            {
                uniqueHelperNames = new Dictionary<string, IdentifierNode>();
            }
            externalHelpersModuleName = emitContext.GetExternalHelpersModuleName(sourceFile);
            SetSourceMapSource(SourceFileSourceMapSource.For(sourceFile));
        }

        // !!!
    }

    public void Write(Node node, SourceFile? sourceFile, EmitTextWriter writer, global::Ts.SourceMap.Generator? sourceMapGenerator)
    {
        var savedCurrentSourceFile = currentSourceFile;
        var savedWriter = this.writer;
        var savedUniqueHelperNames = uniqueHelperNames;
        var savedSourceMapsDisabled = sourceMapsDisabled;
        var savedSourceMapGenerator = this.sourceMapGenerator;
        var savedSourceMapSource = sourceMapSource;
        var savedSourceMapSourceIndex = sourceMapSourceIndex;
        var savedSourceMapLineCharCache = sourceMapLineCharCache;

        sourceMapsDisabled = sourceMapGenerator is null;
        this.sourceMapGenerator = sourceMapGenerator;
        sourceMapSource = null;
        sourceMapSourceIndex = -1;
        sourceMapLineCharCache = null;

        SetSourceFile(sourceFile);
        this.writer = writer;
        this.writer.Clear();
        if (sourceFile is not null)
        {
            // Go: if grower, ok := p.writer.(interface{ Grow(n int) }); ok { grower.Grow(len(sourceFile.Text())) }
            // The EmitTextWriter interface does not expose Grow; the concrete TextWriter does. Best-effort only.
            if (this.writer is TextWriter tw)
            {
                tw.Grow(sourceFile.Text().Length);
            }
        }

        switch (node.Kind)
        {
            // Pseudo-literals
            case SyntaxKind.TemplateHead:
                EmitTemplateHead(node.AsTemplateHead());
                break;
            case SyntaxKind.TemplateMiddle:
                EmitTemplateMiddle(node.AsTemplateMiddle());
                break;
            case SyntaxKind.TemplateTail:
                EmitTemplateTail(node.AsTemplateTail());
                break;

            // Identifiers
            case SyntaxKind.Identifier:
                EmitIdentifierName(node.AsIdentifier());
                break;

            // PrivateIdentifiers
            case SyntaxKind.PrivateIdentifier:
                EmitPrivateIdentifier(node.AsPrivateIdentifier());
                break;

            // Names
            case SyntaxKind.QualifiedName:
                EmitQualifiedName(node.AsQualifiedName());
                break;
            case SyntaxKind.ComputedPropertyName:
                EmitComputedPropertyName(node.AsComputedPropertyName());
                break;

            // Signature elements
            case SyntaxKind.TypeParameter:
                EmitTypeParameter(node.AsTypeParameterDeclaration());
                break;
            case SyntaxKind.Parameter:
                EmitParameter(node.AsParameterDeclaration());
                break;
            case SyntaxKind.Decorator:
                EmitDecorator(node.AsDecorator());
                break;

            // Type members
            case SyntaxKind.PropertySignature:
                EmitPropertySignature(node.AsPropertySignatureDeclaration());
                break;
            case SyntaxKind.PropertyDeclaration:
                EmitPropertyDeclaration(node.AsPropertyDeclaration());
                break;
            case SyntaxKind.MethodSignature:
                EmitMethodSignature(node.AsMethodSignatureDeclaration());
                break;
            case SyntaxKind.MethodDeclaration:
                EmitMethodDeclaration(node.AsMethodDeclaration());
                break;
            case SyntaxKind.ClassStaticBlockDeclaration:
                EmitClassStaticBlockDeclaration(node.AsClassStaticBlockDeclaration());
                break;
            case SyntaxKind.Constructor:
                EmitConstructor(node.AsConstructorDeclaration());
                break;
            case SyntaxKind.GetAccessor:
                EmitGetAccessorDeclaration(node.AsGetAccessorDeclaration());
                break;
            case SyntaxKind.SetAccessor:
                EmitSetAccessorDeclaration(node.AsSetAccessorDeclaration());
                break;
            case SyntaxKind.CallSignature:
                EmitCallSignature(node.AsCallSignatureDeclaration());
                break;
            case SyntaxKind.ConstructSignature:
                EmitConstructSignature(node.AsConstructSignatureDeclaration());
                break;
            case SyntaxKind.IndexSignature:
                EmitIndexSignature(node.AsIndexSignatureDeclaration());
                break;

            // Binding patterns
            case SyntaxKind.ObjectBindingPattern:
                EmitObjectBindingPattern(node.AsBindingPattern());
                break;
            case SyntaxKind.ArrayBindingPattern:
                EmitArrayBindingPattern(node.AsBindingPattern());
                break;
            case SyntaxKind.BindingElement:
                EmitBindingElement(node.AsBindingElement());
                break;

            // Misc
            case SyntaxKind.TemplateSpan:
                EmitTemplateSpan(node.AsTemplateSpan());
                break;
            case SyntaxKind.SemicolonClassElement:
                EmitSemicolonClassElement(node.AsSemicolonClassElement());
                break;

            // Declarations (non-statement)
            case SyntaxKind.VariableDeclaration:
                EmitVariableDeclaration(node.AsVariableDeclaration());
                break;
            case SyntaxKind.VariableDeclarationList:
                EmitVariableDeclarationList(node.AsVariableDeclarationList());
                break;
            case SyntaxKind.ModuleBlock:
                EmitModuleBlock(node.AsModuleBlock());
                break;
            case SyntaxKind.CaseBlock:
                EmitCaseBlock(node.AsCaseBlock());
                break;
            case SyntaxKind.ImportClause:
                EmitImportClause(node.AsImportClause());
                break;
            case SyntaxKind.NamespaceImport:
                EmitNamespaceImport(node.AsNamespaceImport());
                break;
            case SyntaxKind.NamespaceExport:
                EmitNamespaceExport(node.AsNamespaceExport());
                break;
            case SyntaxKind.NamedImports:
                EmitNamedImports(node.AsNamedImports());
                break;
            case SyntaxKind.ImportSpecifier:
                EmitImportSpecifier(node.AsImportSpecifier());
                break;
            case SyntaxKind.NamedExports:
                EmitNamedExports(node.AsNamedExports());
                break;
            case SyntaxKind.ExportSpecifier:
                EmitExportSpecifier(node.AsExportSpecifier());
                break;
            case SyntaxKind.ImportAttributes:
                EmitImportAttributes(node.AsImportAttributes());
                break;
            case SyntaxKind.ImportAttribute:
                EmitImportAttribute(node.AsImportAttribute());
                break;

            // Module references
            case SyntaxKind.ExternalModuleReference:
                EmitExternalModuleReference(node.AsExternalModuleReference());
                break;

            // JSX (non-expression)
            case SyntaxKind.JsxText:
                EmitJsxText(node.AsJsxText());
                break;
            case SyntaxKind.JsxOpeningElement:
                EmitJsxOpeningElement(node.AsJsxOpeningElement());
                break;
            case SyntaxKind.JsxOpeningFragment:
                EmitJsxOpeningFragment(node.AsJsxOpeningFragment());
                break;
            case SyntaxKind.JsxClosingElement:
                EmitJsxClosingElement(node.AsJsxClosingElement());
                break;
            case SyntaxKind.JsxClosingFragment:
                EmitJsxClosingFragment(node.AsJsxClosingFragment());
                break;
            case SyntaxKind.JsxAttribute:
                EmitJsxAttribute(node.AsJsxAttribute());
                break;
            case SyntaxKind.JsxAttributes:
                EmitJsxAttributes(node.AsJsxAttributes());
                break;
            case SyntaxKind.JsxSpreadAttribute:
                EmitJsxSpreadAttribute(node.AsJsxSpreadAttribute());
                break;
            case SyntaxKind.JsxExpression:
                EmitJsxExpression(node.AsJsxExpression());
                break;
            case SyntaxKind.JsxNamespacedName:
                EmitJsxNamespacedName(node.AsJsxNamespacedName());
                break;

            // Clauses
            case SyntaxKind.CaseClause:
                EmitCaseClause(node.AsCaseOrDefaultClause());
                break;
            case SyntaxKind.DefaultClause:
                EmitDefaultClause(node.AsCaseOrDefaultClause());
                break;
            case SyntaxKind.HeritageClause:
                EmitHeritageClause(node.AsHeritageClause());
                break;
            case SyntaxKind.CatchClause:
                EmitCatchClause(node.AsCatchClause());
                break;

            // Property assignments
            case SyntaxKind.PropertyAssignment:
                EmitPropertyAssignment(node.AsPropertyAssignment());
                break;
            case SyntaxKind.ShorthandPropertyAssignment:
                EmitShorthandPropertyAssignment(node.AsShorthandPropertyAssignment());
                break;
            case SyntaxKind.SpreadAssignment:
                EmitSpreadAssignment(node.AsSpreadAssignment());
                break;

            // Enum
            case SyntaxKind.EnumMember:
                EmitEnumMember(node.AsEnumMember());
                break;

            // Top-level nodes
            case SyntaxKind.SourceFile:
                EmitSourceFileWorker(node.AsSourceFile());
                break;

            // Transformation nodes
            case SyntaxKind.NotEmittedTypeElement:
                EmitNotEmittedTypeElement(node.AsNotEmittedTypeElement());
                break;

            default:
                if (AstUtil.IsTypeNode(node))
                {
                    EmitTypeNodeOutsideExtends(node);
                }
                else if (AstUtil.IsStatement(node))
                {
                    EmitStatement(node);
                }
                else if (AstUtil.IsExpression(node))
                {
                    EmitExpression(node, OperatorPrecedence.Lowest);
                }
                else if (NodeTests.IsKeywordKind(node.Kind))
                {
                    EmitKeywordNode(node);
                }
                else if (NodeTests.IsPunctuationKind(node.Kind))
                {
                    EmitPunctuationNode(node);
                }
                else if (AstUtil.IsJSDocKind(node.Kind))
                {
                    EmitJSDocNode(node);
                }
                else
                {
                    throw new InvalidOperationException($"unhandled Node: {node.Kind}");
                }
                break;
        }

        currentSourceFile = savedCurrentSourceFile;
        this.writer = savedWriter;
        uniqueHelperNames = savedUniqueHelperNames;
        sourceMapsDisabled = savedSourceMapsDisabled;
        sourceMapGenerator = savedSourceMapGenerator;
        sourceMapSource = savedSourceMapSource;
        sourceMapSourceIndex = savedSourceMapSourceIndex;
        sourceMapLineCharCache = savedSourceMapLineCharCache;
    }

    //
    // Comments
    //

    private CommentState? EmitCommentsBeforeNode(Node node)
    {
        if (!ShouldEmitComments(node))
        {
            return null;
        }

        var emitFlags = emitContext.EmitFlags(node);
        var commentRange = emitContext.CommentRange(node);
        var containerPos = this.containerPos;
        var containerEnd = this.containerEnd;
        var declarationListContainerEnd = this.declarationListContainerEnd;

        // Emit leading comments
        EmitLeadingCommentsOfNode(node, emitFlags, commentRange);
        EmitLeadingSyntheticCommentsOfNode(node, emitFlags);
        if ((emitFlags & EF.NoNestedComments) != 0)
        {
            commentsDisabled = true;
        }

        return new CommentState
        {
            EmitFlags = emitFlags,
            CommentRange = commentRange,
            ContainerPos = containerPos,
            ContainerEnd = containerEnd,
            DeclarationListContainerEnd = declarationListContainerEnd,
        };
    }

    private void EmitCommentsAfterNode(Node node, CommentState? state)
    {
        if (state is null)
        {
            return;
        }

        var emitFlags = state.EmitFlags;
        var commentRange = state.CommentRange;
        var containerPos = state.ContainerPos;
        var containerEnd = state.ContainerEnd;
        var declarationListContainerEnd = state.DeclarationListContainerEnd;

        if ((emitFlags & EF.NoNestedComments) != 0)
        {
            commentsDisabled = false;
        }

        EmitTrailingSyntheticCommentsOfNode(node, emitFlags);
        EmitTrailingCommentsOfNode(node, emitFlags, commentRange, containerPos, containerEnd, declarationListContainerEnd);

        // Preserve comments from erased type annotation
        var typeNode = emitContext.GetTypeNode(node);
        if (typeNode is not null)
        {
            EmitTrailingCommentsOfNode(node, emitFlags, typeNode.Loc, containerPos, containerEnd, declarationListContainerEnd);
        }
    }

    private (CommentState? State, int Pos) EmitCommentsBeforeToken(SyntaxKind token, int pos, Node contextNode, TokenEmitFlags flags)
    {
        if ((flags & TokenEmitFlags.NoComments) != 0 || commentsDisabled)
        {
            if (currentSourceFile is not null && !AstUtil.PositionIsSynthesized(pos))
            {
                pos = Scanner.SkipTrivia(currentSourceFile.Text(), pos);
            }
            return (null, pos);
        }

        int startPos = pos;
        if (currentSourceFile is not null)
        {
            pos = Scanner.SkipTrivia(currentSourceFile.Text(), startPos);
        }

        var node = emitContext.ParseNode(contextNode);
        bool isSimilarNode = node is not null && node.Kind == contextNode.Kind;
        if (!isSimilarNode)
        {
            return (null, pos);
        }

        if (contextNode.Pos != startPos)
        {
            bool indentLeading = (flags & TokenEmitFlags.IndentLeadingComments) != 0;
            bool needsIndent = indentLeading && currentSourceFile is not null && !PrinterUtilities.PositionsAreOnSameLine(startPos, pos, currentSourceFile);
            IncreaseIndentIf(needsIndent);
            EmitLeadingComments(startPos, false /*elided*/);
            DecreaseIndentIf(needsIndent);
        }

        return (new CommentState(), pos);
    }

    private void EmitCommentsAfterToken(SyntaxKind token, int pos, Node contextNode, CommentState? state)
    {
        if (state is null)
        {
            return;
        }

        if (contextNode.End != pos)
        {
            bool isJsxExprContext = contextNode.Kind == SyntaxKind.JsxExpression;
            EmitTrailingComments(pos, isJsxExprContext ? CommentSeparator.None : CommentSeparator.Before);
        }
    }

    private CommentState? EmitDetachedCommentsBeforeStatementList(Node node, TextRange detachedRange)
    {
        if (!ShouldEmitDetachedComments(node))
        {
            return null;
        }

        var emitFlags = emitContext.EmitFlags(node);
        var containerPos = this.containerPos;
        var containerEnd = this.containerEnd;
        var declarationListContainerEnd = this.declarationListContainerEnd;
        bool skipLeadingComments = AstUtil.PositionIsSynthesized(detachedRange.Pos) || (emitFlags & EF.NoLeadingComments) != 0;

        if (!skipLeadingComments)
        {
            EmitDetachedCommentsAndUpdateCommentsInfo(detachedRange);
        }

        if ((emitFlags & EF.NoNestedComments) != 0)
        {
            commentsDisabled = true;
        }

        return new CommentState
        {
            EmitFlags = emitFlags,
            CommentRange = detachedRange,
            ContainerPos = containerPos,
            ContainerEnd = containerEnd,
            DeclarationListContainerEnd = declarationListContainerEnd,
        };
    }

    private void EmitDetachedCommentsAfterStatementList(Node node, TextRange detachedRange, CommentState? state)
    {
        if (state is null)
        {
            return;
        }

        var emitFlags = state.EmitFlags;
        bool skipTrailingComments = commentsDisabled || AstUtil.PositionIsSynthesized(detachedRange.End) || (emitFlags & EF.NoTrailingComments) != 0;

        if (!skipTrailingComments)
        {
            bool hasWrittenComment = EmitLeadingComments(detachedRange.End, false /*elided*/);
            if (hasWrittenComment && !writer.IsAtStartOfLine())
            {
                WriteLine();
            }
        }
    }

    private void EmitLeadingCommentsOfNode(Node node, EmitFlags emitFlags, TextRange commentRange)
    {
        int pos = commentRange.Pos;
        int end = commentRange.End;

        if ((!AstUtil.PositionIsSynthesized(pos) || !AstUtil.PositionIsSynthesized(end)) && pos != end)
        {
            bool skipLeadingComments = AstUtil.PositionIsSynthesized(pos) || (emitFlags & EF.NoLeadingComments) != 0 || node.Kind == SyntaxKind.JsxText;
            bool skipTrailingComments = AstUtil.PositionIsSynthesized(end) || (emitFlags & EF.NoTrailingComments) != 0 || node.Kind == SyntaxKind.JsxText;

            if (!skipLeadingComments)
            {
                EmitLeadingComments(pos, node.Kind == SyntaxKind.NotEmittedStatement /*elided*/);
            }

            if (!skipLeadingComments || (pos >= 0 && (emitFlags & EF.NoLeadingComments) != 0))
            {
                containerPos = pos;
            }

            if (!skipTrailingComments || (end >= 0 && (emitFlags & EF.NoTrailingComments) != 0))
            {
                containerEnd = end;

                if (node.Kind == SyntaxKind.VariableDeclarationList)
                {
                    declarationListContainerEnd = end;
                }
            }
        }
    }

    private void EmitTrailingCommentsOfNode(Node node, EmitFlags emitFlags, TextRange commentRange, int containerPos, int containerEnd, int declarationListContainerEnd)
    {
        int pos = commentRange.Pos;
        int end = commentRange.End;
        bool skipTrailingComments = end < 0 || (emitFlags & EF.NoTrailingComments) != 0 || node.Kind == SyntaxKind.JsxText;
        if ((!AstUtil.PositionIsSynthesized(pos) || !AstUtil.PositionIsSynthesized(end)) && pos != end)
        {
            this.containerPos = containerPos;
            this.containerEnd = containerEnd;
            this.declarationListContainerEnd = declarationListContainerEnd;

            if (!skipTrailingComments && node.Kind != SyntaxKind.NotEmittedStatement)
            {
                EmitTrailingComments(end, CommentSeparator.Before);
            }
        }
    }

    private void EmitLeadingSyntheticCommentsOfNode(Node node, EmitFlags emitFlags)
    {
        if ((emitFlags & EF.NoLeadingComments) != 0)
        {
            return;
        }
        var synth = emitContext.GetSyntheticLeadingComments(node);
        if (synth is not null)
        {
            foreach (var c in synth)
            {
                EmitLeadingSynthesizedComment(c);
            }
        }
    }

    private void EmitLeadingSynthesizedComment(SynthesizedComment comment)
    {
        if (comment.HasLeadingNewLine || comment.Kind == SyntaxKind.SingleLineCommentTrivia)
        {
            writer.WriteLine();
        }
        WriteSynthesizedComment(comment);
        if (comment.HasTrailingNewLine || comment.Kind == SyntaxKind.SingleLineCommentTrivia)
        {
            writer.WriteLine();
        }
        else
        {
            writer.WriteSpace(" ");
        }
    }

    private void EmitTrailingSyntheticCommentsOfNode(Node node, EmitFlags emitFlags)
    {
        if ((emitFlags & EF.NoTrailingComments) != 0)
        {
            return;
        }
        var synth = emitContext.GetSyntheticTrailingComments(node);
        if (synth is not null)
        {
            foreach (var c in synth)
            {
                EmitTrailingSynthesizedComment(c);
            }
        }
    }

    private void EmitTrailingSynthesizedComment(SynthesizedComment comment)
    {
        if (!writer.IsAtStartOfLine())
        {
            writer.WriteSpace(" ");
        }
        WriteSynthesizedComment(comment);
        if (comment.HasTrailingNewLine)
        {
            writer.WriteLine();
        }
    }

    private static string FormatSynthesizedComment(SynthesizedComment comment)
    {
        if (comment.Kind == SyntaxKind.MultiLineCommentTrivia)
        {
            return "/*" + comment.Text + "*/";
        }
        return "//" + comment.Text;
    }

    private void WriteSynthesizedComment(SynthesizedComment comment)
    {
        var text = FormatSynthesizedComment(comment);
        int[] lineMap = System.Array.Empty<int>();
        if (comment.Kind == SyntaxKind.MultiLineCommentTrivia)
        {
            lineMap = global::Ts.Core.CoreUtil.ComputeECMALineStarts(text);
        }
        WriteCommentRangeWorker(text, lineMap, comment.Kind, new TextRange(0, text.Length));
    }

    private bool EmitLeadingComments(int pos, bool elided)
    {
        if (commentsDisabled || currentSourceFile is null || AstUtil.PositionIsSynthesized(pos) || pos == containerPos)
        {
            return false;
        }

        var tripleSlash = Tristate.Unknown;
        if (!elided)
        {
            if (pos == 0 && currentSourceFile is not null && currentSourceFile.IsDeclarationFile)
            {
                tripleSlash = Tristate.False;
            }
        }
        else if (pos == 0)
        {
            tripleSlash = Tristate.True;
        }
        else
        {
            return false;
        }

        // skip detached comments
        if (detachedCommentsInfo.Len > 0)
        {
            var info = detachedCommentsInfo.Peek();
            if (info.NodePos == pos)
            {
                pos = detachedCommentsInfo.Pop().DetachedCommentEndPos;
            }
        }

        List<CommentRange>? comments = null;
        foreach (var comment in global::Ts.Parser.CommentScanner.GetLeadingCommentRanges(emitContext.Factory.AsNodeFactory(), currentSourceFile!.Text(), pos))
        {
            if (ShouldWriteComment(comment) && ShouldEmitCommentIfTripleSlash(comment, tripleSlash))
            {
                (comments ??= new List<CommentRange>()).Add(comment);
            }
        }

        if (comments is not null && comments.Count > 0 && ShouldEmitNewLineBeforeLeadingCommentOfPosition(pos, comments[0].Pos))
        {
            WriteLine();
        }

        return EmitComments(comments, CommentSeparator.After);
    }

    private bool ShouldEmitCommentIfTripleSlash(CommentRange comment, Tristate tripleSlash)
    {
        switch (tripleSlash)
        {
            case Tristate.True:
                return IsTripleSlashComment(comment);
            case Tristate.False:
                return !IsTripleSlashComment(comment);
            default:
                return true;
        }
    }

    private bool ShouldEmitNewLineBeforeLeadingCommentOfPosition(int pos, int commentPos)
    {
        return currentSourceFile is not null &&
            pos != commentPos &&
            Scanner.ComputeLineOfPosition(currentSourceFile.ECMALineMap(), pos) != Scanner.ComputeLineOfPosition(currentSourceFile.ECMALineMap(), commentPos);
    }

    private void EmitLeadingCommentsOfPosition(int pos)
    {
        if (commentsDisabled || pos == -1)
        {
            return;
        }

        EmitLeadingComments(pos, false /*elided*/);
    }

    private void EmitTrailingComments(int pos, CommentSeparator commentSeparator)
    {
        if (commentsDisabled)
        {
            return;
        }
        if (commentsDisabled || currentSourceFile is null || (containerEnd != -1 && (pos == containerEnd || pos == declarationListContainerEnd)))
        {
            return;
        }

        List<CommentRange>? comments = null;
        foreach (var comment in global::Ts.Parser.CommentScanner.GetTrailingCommentRanges(emitContext.Factory.AsNodeFactory(), currentSourceFile.Text(), pos))
        {
            if (ShouldWriteComment(comment))
            {
                (comments ??= new List<CommentRange>()).Add(comment);
            }
        }

        EmitComments(comments, commentSeparator);
    }

    private void EmitTrailingCommentsOfPosition(int pos, bool prefixSpace, bool forceNoNewline)
    {
        if (commentsDisabled || currentSourceFile is null)
        {
            return;
        }
        if (containerEnd != -1 && (pos == containerEnd || pos == declarationListContainerEnd))
        {
            return;
        }

        List<CommentRange> comments = new();
        foreach (var comment in global::Ts.Parser.CommentScanner.GetTrailingCommentRanges(emitContext.Factory.AsNodeFactory(), currentSourceFile.Text(), pos))
        {
            comments.Add(comment);
        }
        if (comments.Count == 0)
        {
            return;
        }

        foreach (var comment in comments)
        {
            if (prefixSpace)
            {
                if (!ShouldWriteComment(comment))
                {
                    continue;
                }
                if (!writer.IsAtStartOfLine())
                {
                    WriteSpace();
                }
                EmitComment(comment);
                if (comment.HasTrailingNewLine)
                {
                    WriteLine();
                }
                continue;
            }

            EmitComment(comment);
            if (forceNoNewline)
            {
                if (comment.Kind == SyntaxKind.SingleLineCommentTrivia)
                {
                    WriteLine();
                }
            }
            else if (comment.HasTrailingNewLine)
            {
                WriteLine();
            }
            else
            {
                WriteSpace();
            }
        }
    }

    private void EmitDetachedCommentsAndUpdateCommentsInfo(TextRange textRange)
    {
        if (currentSourceFile is null)
        {
            return;
        }
        if (EmitDetachedComments(textRange, out var currentDetachedCommentInfo))
        {
            detachedCommentsInfo.Push(currentDetachedCommentInfo);
        }
    }

    private bool EmitDetachedComments(TextRange textRange, out DetachedCommentsInfo result)
    {
        result = default;
        if (currentSourceFile is null)
        {
            return false;
        }

        var text = currentSourceFile.Text();
        var lineMap = currentSourceFile.ECMALineMap();

        List<CommentRange> leadingComments = new();
        if (commentsDisabled)
        {
            if (textRange.Pos == 0)
            {
                foreach (var comment in global::Ts.Parser.CommentScanner.GetLeadingCommentRanges(emitContext.Factory.AsNodeFactory(), text, textRange.Pos))
                {
                    if (PrinterUtilities.IsPinnedComment(text, comment))
                    {
                        leadingComments.Add(comment);
                    }
                }
            }
        }
        else
        {
            leadingComments = global::Ts.Parser.CommentScanner.GetLeadingCommentRanges(emitContext.Factory.AsNodeFactory(), text, textRange.Pos).ToList();
        }

        if (leadingComments.Count > 0)
        {
            List<CommentRange> detachedComments = new();
            CommentRange lastComment = default;
            for (int i = 0; i < leadingComments.Count; i++)
            {
                var comment = leadingComments[i];
                if (i > 0)
                {
                    int lastCommentLine = Scanner.ComputeLineOfPosition(lineMap, lastComment.End);
                    int commentLine = Scanner.ComputeLineOfPosition(lineMap, comment.Pos);

                    if (commentLine >= lastCommentLine + 2)
                    {
                        break;
                    }
                }

                detachedComments.Add(comment);
                lastComment = comment;
            }

            if (detachedComments.Count > 0)
            {
                int lastCommentLine = Scanner.ComputeLineOfPosition(lineMap, detachedComments[^1].End);
                int nodeLine = Scanner.ComputeLineOfPosition(lineMap, Scanner.SkipTrivia(text, textRange.Pos));
                if (nodeLine >= lastCommentLine + 2)
                {
                    // Valid detachedComments
                    List<CommentRange> commentsToEmit = new();
                    foreach (var comment in detachedComments)
                    {
                        if (ShouldWriteComment(comment))
                        {
                            commentsToEmit.Add(comment);
                        }
                    }

                    if (commentsToEmit.Count > 0)
                    {
                        if (ShouldEmitNewLineBeforeLeadingCommentOfPosition(textRange.Pos, commentsToEmit[0].Pos))
                        {
                            WriteLine();
                        }

                        EmitComments(commentsToEmit, CommentSeparator.After);
                    }
                    result = new DetachedCommentsInfo { NodePos = textRange.Pos, DetachedCommentEndPos = detachedComments[^1].End };
                    return true;
                }
            }
        }
        return false;
    }

    private bool EmitComments(List<CommentRange>? comments, CommentSeparator commentSeparator)
    {
        bool interveningSeparator = false;
        if (comments is null || comments.Count == 0)
        {
            return false;
        }

        if (commentSeparator == CommentSeparator.Before)
        {
            WriteSpace();
        }

        foreach (var comment in comments)
        {
            if (interveningSeparator)
            {
                WriteSpace();
                interveningSeparator = false;
            }

            EmitComment(comment);

            if (comment.Kind == SyntaxKind.SingleLineCommentTrivia || comment.HasTrailingNewLine && commentSeparator != CommentSeparator.None)
            {
                WriteLine();
            }
            else
            {
                interveningSeparator = commentSeparator != CommentSeparator.None;
            }
        }

        if (interveningSeparator && commentSeparator == CommentSeparator.After)
        {
            WriteSpace();
        }

        return true;
    }

    private void EmitComment(CommentRange comment)
    {
        EmitPos(comment.Pos);
        WriteCommentRange(comment);
        EmitPos(comment.End);
    }

    private bool IsTripleSlashComment(CommentRange comment)
    {
        return currentSourceFile is not null &&
            PrinterUtilities.IsRecognizedTripleSlashComment(currentSourceFile.Text(), comment);
    }

    //
    // Source Maps
    //

    private void SetSourceMapSource(SourceMapSource source)
    {
        if (sourceMapsDisabled)
        {
            return;
        }

        sourceMapSource = source;
        sourceMapLineCharCache = new LineCharacterCache(source);
        if (mostRecentSourceMapSource == source)
        {
            sourceMapSourceIndex = mostRecentSourceMapSourceIndex;
            return;
        }

        sourceMapSourceIsJson = global::Ts.TsPath.TsPath.FileExtensionIs(source.FileName(), global::Ts.TsPath.TsPath.ExtensionJson);
        if (sourceMapSourceIsJson)
        {
            return;
        }

        sourceMapSourceIndex = sourceMapGenerator!.AddSource(source.FileName());
        if (Options.InlineSources)
        {
            var err = sourceMapGenerator.SetSourceContent(sourceMapSourceIndex, source.Text());
            if (err is not null)
            {
                throw new InvalidOperationException(err);
            }
        }

        mostRecentSourceMapSource = source;
        mostRecentSourceMapSourceIndex = sourceMapSourceIndex;
    }

    private void EmitPos(int pos)
    {
        if (sourceMapsDisabled || sourceMapSource is null || sourceMapGenerator is null || sourceMapSourceIsJson || AstUtil.PositionIsSynthesized(pos))
        {
            return;
        }

        var (sourceLine, sourceCharacter) = sourceMapLineCharCache!.GetLineAndCharacter(pos);
        var err = sourceMapGenerator.AddSourceMapping(
            writer.GetLine(),
            writer.GetColumn(),
            sourceMapSourceIndex,
            sourceLine,
            sourceCharacter);
        if (err is not null)
        {
            throw new InvalidOperationException(err);
        }
    }

    private void EmitSourcePos(SourceMapSource? source, int pos)
    {
        if (source != sourceMapSource)
        {
            var savedSourceMapSource = sourceMapSource;
            var savedSourceMapSourceIndex = sourceMapSourceIndex;
            var savedSourceMapLineCharCache = sourceMapLineCharCache;
            SetSourceMapSource(source!);
            EmitPos(pos);
            sourceMapSource = savedSourceMapSource;
            sourceMapSourceIndex = savedSourceMapSourceIndex;
            sourceMapLineCharCache = savedSourceMapLineCharCache;
        }
        else
        {
            EmitPos(pos);
        }
    }

    private SourceMapState? EmitSourceMapsBeforeNode(Node node)
    {
        if (!ShouldEmitSourceMaps(node))
        {
            return null;
        }

        var emitFlags = emitContext.EmitFlags(node);
        var loc = emitContext.SourceMapRange(node);

        if (!NodeTests.IsNotEmittedStatement(node) &&
            (emitFlags & EF.NoLeadingSourceMap) == 0 &&
            currentSourceFile is not null &&
            !AstUtil.PositionIsSynthesized(loc.Pos))
        {
            EmitSourcePos(sourceMapSource, Scanner.SkipTrivia(currentSourceFile.Text(), loc.Pos));
        }

        if ((emitFlags & EF.NoNestedSourceMaps) != 0)
        {
            sourceMapsDisabled = true;
        }

        return new SourceMapState { EmitFlags = emitFlags, SourceMapRange = loc, HasTokenSourceMapRange = false };
    }

    private void EmitSourceMapsAfterNode(Node node, SourceMapState? previousState)
    {
        if (previousState is null)
        {
            return;
        }

        var emitFlags = previousState.EmitFlags;
        var loc = previousState.SourceMapRange;

        if ((emitFlags & EF.NoNestedSourceMaps) != 0)
        {
            sourceMapsDisabled = false;
        }

        if (!NodeTests.IsNotEmittedStatement(node) &&
            (emitFlags & EF.NoTrailingSourceMap) == 0 &&
            !AstUtil.PositionIsSynthesized(loc.End))
        {
            EmitSourcePos(sourceMapSource, loc.End);
        }
    }

    private SourceMapState? EmitSourceMapsBeforeToken(SyntaxKind token, int pos, Node contextNode, TokenEmitFlags flags)
    {
        if (!ShouldEmitTokenSourceMaps(token, pos, contextNode, flags))
        {
            return null;
        }

        var emitFlags = emitContext.EmitFlags(contextNode);
        bool hasLoc = emitContext.TryTokenSourceMapRange(contextNode, token, out var loc);
        if (hasLoc)
        {
            pos = loc.Pos;
        }
        if (pos >= 0 && currentSourceFile is not null)
        {
            pos = Scanner.SkipTrivia(currentSourceFile.Text(), pos);
        }
        if ((emitFlags & EF.NoTokenLeadingSourceMaps) == 0 && pos >= 0)
        {
            EmitSourcePos(sourceMapSource, pos);
        }

        return new SourceMapState { EmitFlags = emitFlags, SourceMapRange = loc, HasTokenSourceMapRange = hasLoc };
    }

    private void EmitSourceMapsAfterToken(SyntaxKind token, int pos, Node contextNode, SourceMapState? previousState)
    {
        if (previousState is null)
        {
            return;
        }

        var emitFlags = previousState.EmitFlags;
        var loc = previousState.SourceMapRange;
        bool hasLoc = previousState.HasTokenSourceMapRange;
        if ((emitFlags & EF.NoTokenTrailingSourceMaps) == 0)
        {
            if (hasLoc)
            {
                pos = loc.End;
            }
            if (pos >= 0)
            {
                EmitSourcePos(sourceMapSource, pos);
            }
        }
    }

    //
    // Name Generation
    //

    private bool ShouldReuseTempVariableScope(Node? node)
    {
        return node is not null && (emitContext.EmitFlags(node) & EF.ReuseTempVariableScope) != 0;
    }

    private void PushNameGenerationScope(Node? node)
    {
        nameGenerator.PushScope(ShouldReuseTempVariableScope(node));
    }

    private void PopNameGenerationScope(Node? node)
    {
        nameGenerator.PopScope(ShouldReuseTempVariableScope(node));
    }

    private void GenerateAllNames(NodeList? nodes)
    {
        if (nodes is null)
        {
            return;
        }
        foreach (var node in nodes.Nodes)
        {
            GenerateNames(node);
        }
    }

    private void GenerateNames(Node? node)
    {
        if (node is null)
        {
            return;
        }

        switch (node.Kind)
        {
            case SyntaxKind.Block:
            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
                GenerateAllNames(node.StatementList());
                break;
            case SyntaxKind.LabeledStatement:
            case SyntaxKind.WithStatement:
            case SyntaxKind.DoStatement:
            case SyntaxKind.WhileStatement:
                GenerateNames(node.Statement());
                break;
            case SyntaxKind.IfStatement:
                GenerateNames(node.AsIfStatement().ThenStatement);
                GenerateNames(node.AsIfStatement().ElseStatement);
                break;
            case SyntaxKind.ForStatement:
            case SyntaxKind.ForOfStatement:
            case SyntaxKind.ForInStatement:
                GenerateNames(node.Initializer());
                GenerateNames(node.Statement());
                break;
            case SyntaxKind.SwitchStatement:
                GenerateNames(node.AsSwitchStatement().CaseBlock);
                break;
            case SyntaxKind.CaseBlock:
                GenerateAllNames(node.AsCaseBlock().Clauses);
                break;
            case SyntaxKind.TryStatement:
                GenerateNames(node.AsTryStatement().TryBlock);
                GenerateNames(node.AsTryStatement().CatchClause);
                GenerateNames(node.AsTryStatement().FinallyBlock);
                break;
            case SyntaxKind.CatchClause:
                GenerateNames(node.AsCatchClause().VariableDeclaration);
                GenerateNames(node.AsCatchClause().Block);
                break;
            case SyntaxKind.VariableStatement:
                GenerateNames(node.AsVariableStatement().DeclarationList);
                break;
            case SyntaxKind.VariableDeclarationList:
                GenerateAllNames(node.AsVariableDeclarationList().Declarations);
                break;
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.Parameter:
            case SyntaxKind.BindingElement:
            case SyntaxKind.ClassDeclaration:
                GenerateNameIfNeeded(node.Name());
                break;
            case SyntaxKind.FunctionDeclaration:
                GenerateNameIfNeeded(node.Name());
                if (ShouldReuseTempVariableScope(node))
                {
                    GenerateAllNames(node.AsFunctionDeclaration().Parameters);
                    GenerateNames(node.AsFunctionDeclaration().Body);
                }
                break;
            case SyntaxKind.ObjectBindingPattern:
            case SyntaxKind.ArrayBindingPattern:
                GenerateAllNames(node.ElementList());
                break;
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
                GenerateNames(node.AsImportDeclaration().ImportClause);
                break;
            case SyntaxKind.ImportClause:
                GenerateNameIfNeeded(node.AsImportClause().Name());
                GenerateNames(node.AsImportClause().NamedBindings);
                break;
            case SyntaxKind.NamespaceImport:
            case SyntaxKind.NamespaceExport:
                GenerateNameIfNeeded(node.Name());
                break;
            case SyntaxKind.NamedImports:
                GenerateAllNames(node.ElementList());
                break;
            case SyntaxKind.ImportSpecifier:
            {
                var n = node.AsImportSpecifier();
                if (n.PropertyName is not null)
                {
                    GenerateNameIfNeeded(n.PropertyName);
                }
                else
                {
                    GenerateNameIfNeeded(n.Name());
                }
                break;
            }
        }
    }

    private void GenerateAllMemberNames(NodeList? nodes)
    {
        if (nodes is null)
        {
            return;
        }
        foreach (var node in nodes.Nodes)
        {
            GenerateMemberNames(node);
        }
    }

    private void GenerateMemberNames(Node? node)
    {
        if (node is null)
        {
            return;
        }
        switch (node.Kind)
        {
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.ShorthandPropertyAssignment:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                GenerateNameIfNeeded(node.Name());
                break;
        }
    }

    private void GenerateNameIfNeeded(Node? name)
    {
        if (name is not null)
        {
            if (AstUtil.IsMemberName(name))
            {
                GenerateName(name);
            }
            else if (AstUtil.IsBindingPattern(name))
            {
                GenerateNames(name);
            }
        }
    }

    private void GenerateName(Node name)
    {
        _ = nameGenerator.GenerateName(name);
    }

    private bool IsFileLevelUniqueNameInCurrentFile(string name, bool privateName)
    {
        if (currentSourceFile is not null)
        {
            return PrinterUtilities.IsFileLevelUniqueName(currentSourceFile, name, HasGlobalName);
        }
        else
        {
            return true;
        }
    }

    //
    // Scoped operations
    //

    private PrinterState EnterNode(Node node)
    {
        var state = new PrinterState();

        OnBeforeEmitNode?.Invoke(node);

        state.CommentState = EmitCommentsBeforeNode(node);
        state.SourceMapState = EmitSourceMapsBeforeNode(node);
        return state;
    }

    private void ExitNode(Node node, PrinterState previousState)
    {
        EmitSourceMapsAfterNode(node, previousState.SourceMapState);
        EmitCommentsAfterNode(node, previousState.CommentState);

        OnAfterEmitNode?.Invoke(node);
    }

    private PrinterState EnterTokenNode(Node node, TokenEmitFlags flags)
    {
        var state = new PrinterState();

        OnBeforeEmitToken?.Invoke(node);

        if ((flags & TokenEmitFlags.NoComments) == 0)
        {
            state.CommentState = EmitCommentsBeforeNode(node);
        }
        if ((flags & TokenEmitFlags.NoSourceMaps) == 0)
        {
            state.SourceMapState = EmitSourceMapsBeforeNode(node);
        }
        return state;
    }

    private void ExitTokenNode(Node node, PrinterState previousState)
    {
        EmitSourceMapsAfterNode(node, previousState.SourceMapState);
        EmitCommentsAfterNode(node, previousState.CommentState);

        OnAfterEmitToken?.Invoke(node);
    }

    private (PrinterState State, int Pos) EnterToken(SyntaxKind token, int pos, Node contextNode, TokenEmitFlags flags)
    {
        var state = new PrinterState();
        var (commentState, newPos) = EmitCommentsBeforeToken(token, pos, contextNode, flags);
        state.CommentState = commentState;
        pos = newPos;
        state.SourceMapState = EmitSourceMapsBeforeToken(token, pos, contextNode, flags);
        return (state, pos);
    }

    private void ExitToken(SyntaxKind token, int pos, Node contextNode, PrinterState previousState)
    {
        EmitSourceMapsAfterToken(token, pos, contextNode, previousState.SourceMapState);
        EmitCommentsAfterToken(token, pos, contextNode, previousState.CommentState);
    }
}
