// Ported from tsgo internal/ls/hover.go — quick info / hover: symbol display construction
// (getQuickInfoAndDeclarationAtLocation), documentation extraction (JSDoc + signature + alias),
// and the shared node/symbol adjustment helpers. GetSymbolDocumentationComment (jsdoc.go:22-45)
// lives here too — it delegates to getDocumentationFromDeclaration.
//
// PORT-NOTE (D slice scope): the LSP ProvideHover shell is represented by GetQuickInfoAtPosition
// (the native entry the C# LSP server routes hover through); vsCapability is always false on this
// path (as in Go's ProvideHover), so the classified VS arms take their Go fallback branch — the
// NewNodeBuilderEx + Printer.IdToSymbol classified printing lands with the VS-capability wave.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Ts.Ast;

namespace Ts.Ls;

public sealed record QuickInfo(string DisplayString, global::Ts.Core.TextRange TextSpan, string Documentation = "");

public sealed partial class LanguageService
{
    // hover.go:20-23
    private const global::Ts.Checker.SymbolFormatFlags HoverSymbolFormatFlags =
        global::Ts.Checker.SymbolFormatFlags.WriteTypeParametersOrArguments |
        global::Ts.Checker.SymbolFormatFlags.UseOnlyExternalAliasing |
        global::Ts.Checker.SymbolFormatFlags.AllowAnyNodeKind |
        global::Ts.Checker.SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope;

    private const global::Ts.Checker.TypeFormatFlags HoverTypeFormatFlags =
        global::Ts.Checker.TypeFormatFlags.UseAliasDefinedOutsideCurrentScope |
        global::Ts.Checker.TypeFormatFlags.UseInstantiationExpressions;

    // hover.go:25-86 — ProvideHover core, minus the LSP envelope (the C# server builds the Hover
    // response from this native shape).
    public QuickInfo? GetQuickInfoAtPosition(
        string fileName,
        int position,
        CancellationToken cancellationToken = default,
        int verbosityLevel = 0,
        int maximumHoverLength = 0,
        bool markdown = false)
    {
        var file = GetProgramAndFile(fileName);
        var node = global::Ts.AstNav.Tokens.GetTouchingPropertyName(file, position);
        if (NodeTests.IsSourceFile(node) || (AstUtilities.IsPropertyAccessOrQualifiedName(node) && LsDeps.IsInComment(file, position, node) is null))
        {
            // Avoid giving quickInfo for the sourceFile as a whole or inside the comment of a/**/.b
            if (NodeTests.IsSourceFile(node))
            {
                return null;
            }
        }
        var (checker, done) = program.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            var rangeNode = GetNodeForQuickInfo(node);
            var symbol = GetSymbolAtLocationForQuickInfo(checker, rangeNode);

            // Always create VerbosityContext for hover so that canExpandSymbol can signal
            // canIncreaseVerbosity even at Level 0. The nodebuilder also detects expandable
            // types at Level 0 via shouldExpandType (maxExpansionDepth = 0).
            var maxTruncLen = maximumHoverLength;
            if (maxTruncLen <= 0)
            {
                maxTruncLen = 500;
            }
            var vc = new global::Ts.Checker.VerbosityContext
            {
                Level = verbosityLevel,
                MaxTruncationLength = maxTruncLen,
            };

            var (quickInfo, documentation) = GetQuickInfoAndDocumentationForSymbol(checker, symbol, rangeNode, markdown, vc);
            if (quickInfo.Length == 0)
            {
                return null;
            }
            var span = new global::Ts.Core.TextRange(
                global::Ts.Scanner.Scanner.GetTokenPosOfNode(rangeNode, file, false /*includeJSDoc*/), rangeNode.End);
            var content = markdown ? FormatQuickInfo(quickInfo) + documentation : quickInfo;
            return new QuickInfo(content, span, documentation);
        }
        finally
        {
            done();
        }
    }

    // hover.go:88-106
    private (string QuickInfo, string Documentation) GetQuickInfoAndDocumentationForSymbol(
        global::Ts.Checker.Checker c, Symbol? symbol, Node node, bool isMarkdown, global::Ts.Checker.VerbosityContext? vc)
    {
        var info = GetQuickInfoAndDeclarationAtLocation(c, symbol, node, vc, false /*vsCapability*/, LsMeaning.GetMeaningFromLocation(node));
        var quickInfo = info.DisplayParts.String();
        if (quickInfo.Length == 0)
        {
            return ("", "");
        }

        var documentation = DocumentationFromSignature(c, symbol, GetCallOrNewExpression(node), node, isMarkdown, false /*commentOnly*/);
        if (documentation.Length != 0)
        {
            return (quickInfo, documentation);
        }

        documentation = GetDocumentationFromDeclaration(c, symbol, info.Declaration, node, isMarkdown, false /*commentOnly*/);
        if (documentation.Length != 0)
        {
            return (quickInfo, documentation);
        }

        return (quickInfo, DocumentationFromAlias(c, symbol, node, isMarkdown));
    }

    // hover.go:108-124
    private string DocumentationFromSignature(global::Ts.Checker.Checker c, Symbol? symbol, Node? node, Node location, bool isMarkdown, bool commentOnly)
    {
        if (node == null)
        {
            return "";
        }
        var signature = c.GetResolvedSignature(node);
        if (signature == null)
        {
            return "";
        }
        var declaration = signature.Declaration();
        if (declaration == null)
        {
            return "";
        }
        if (NodeTests.IsCallSignatureDeclaration(declaration) || NodeTests.IsConstructSignatureDeclaration(declaration))
        {
            return GetDocumentationFromDeclaration(c, symbol, declaration, location, isMarkdown, commentOnly);
        }
        return "";
    }

    // hover.go:126-153
    private string DocumentationFromAlias(global::Ts.Checker.Checker c, Symbol? symbol, Node node, bool isMarkdown)
    {
        if (symbol == null || (symbol.Flags & SymbolFlags.Alias) == 0)
        {
            return "";
        }

        var aliasedSymbol = c.GetAliasedSymbol(symbol);
        if (aliasedSymbol == null || aliasedSymbol == c.GetUnknownSymbol())
        {
            return "";
        }

        var candidates = new List<Symbol> { aliasedSymbol };
        if (aliasedSymbol.ExportSymbol != null)
        {
            candidates.Add(aliasedSymbol.ExportSymbol);
        }

        foreach (var candidate in candidates)
        {
            var aliasedDeclaration = candidate.ValueDeclaration ?? (candidate.Declarations is { Length: > 0 } ds ? ds[0] : null);
            if (aliasedDeclaration == null)
            {
                continue;
            }
            var documentation = GetDocumentationFromDeclaration(c, candidate, aliasedDeclaration, node, isMarkdown, false /*commentOnly*/);
            if (documentation.Length != 0)
            {
                return documentation;
            }
        }

        return "";
    }

    // hover.go:155-243
    internal string GetDocumentationFromDeclaration(global::Ts.Checker.Checker c, Symbol? symbol, Node? declaration, Node location, bool isMarkdown, bool commentOnly)
    {
        if (declaration == null)
        {
            return "";
        }
        var b = new StringBuilder();
        var jsdoc = GetJSDocOrTag(c, declaration);
        if (jsdoc != null && !((declaration.Flags & NodeFlags.Reparsed) == 0 && ContainsTypedefTag(jsdoc)))
        {
            WriteComments(b, c, jsdoc.Comments(), isMarkdown);
            if (jsdoc.Kind == SyntaxKind.JSDoc && !commentOnly)
            {
                if (jsdoc.AsJSDoc().Tags is { } tags)
                {
                    foreach (var tag in tags.Nodes)
                    {
                        if (tag.Kind is SyntaxKind.JSDocTypeTag or SyntaxKind.JSDocTypedefTag or SyntaxKind.JSDocCallbackTag)
                        {
                            continue;
                        }
                        b.Append("\n\n");
                        if (isMarkdown)
                        {
                            b.Append("*@");
                            b.Append(tag.TagName()!.AsIdentifier().Text);
                            b.Append('*');
                        }
                        else
                        {
                            b.Append('@');
                            b.Append(tag.TagName()!.AsIdentifier().Text);
                        }
                        switch (tag.Kind)
                        {
                            case SyntaxKind.JSDocParameterTag:
                            case SyntaxKind.JSDocPropertyTag:
                                WriteOptionalEntityName(b, tag.Name());
                                break;
                            case SyntaxKind.JSDocAugmentsTag:
                                WriteOptionalEntityName(b, tag.AsJSDocAugmentsTag().ClassName);
                                break;
                            case SyntaxKind.JSDocTemplateTag:
                            {
                                var i = 0;
                                foreach (var tp in tag.TypeParameters() ?? [])
                                {
                                    if (i != 0)
                                    {
                                        b.Append(',');
                                    }
                                    WriteOptionalEntityName(b, tp.Name());
                                    i++;
                                }
                                break;
                            }
                        }
                        var comments = tag.Comments();
                        if (tag.Kind == SyntaxKind.JSDocUnknownTag && tag.TagName()!.AsIdentifier().Text == "example")
                        {
                            var commentText = global::Ts.Scanner.Scanner.GetTextOfJSDocComment(tag.CommentList());
                            if (commentText.StartsWith("<caption>", StringComparison.Ordinal))
                            {
                                var captionEnd = commentText.IndexOf("</caption>", StringComparison.Ordinal);
                                if (captionEnd > 0)
                                {
                                    b.Append(" — ");
                                    b.Append(commentText["<caption>".Length..captionEnd]);
                                    commentText = commentText[(captionEnd + "</caption>".Length)..];
                                    // Trim leading blank lines from commentText
                                    while (true)
                                    {
                                        var s1 = commentText.TrimStart(' ', '\t');
                                        var s2 = s1.TrimStart('\r', '\n');
                                        if (s1.Length == s2.Length)
                                        {
                                            break;
                                        }
                                        commentText = s2;
                                    }
                                }
                            }
                            b.Append('\n');
                            if (commentText.Length > 6 && commentText.StartsWith("```", StringComparison.Ordinal) && commentText.EndsWith("```", StringComparison.Ordinal) && commentText.Contains('\n'))
                            {
                                b.Append(commentText);
                                b.Append('\n');
                            }
                            else
                            {
                                WriteCode(b, "tsx", commentText);
                            }
                        }
                        else if (tag.Kind == SyntaxKind.JSDocSeeTag && tag.AsJSDocSeeTag().NameExpression != null)
                        {
                            b.Append(" — ");
                            WriteNameLink(b, c, tag.AsJSDocSeeTag().NameExpression!.Name()!, "", false /*quote*/, isMarkdown);
                            if (comments is { Length: > 0 })
                            {
                                b.Append(' ');
                                WriteComments(b, c, comments, isMarkdown);
                            }
                        }
                        else if (tag.Kind == SyntaxKind.JSDocThrowsTag && tag.AsJSDocThrowsTag().TypeExpression != null)
                        {
                            b.Append(" — ");
                            b.Append(global::Ts.Scanner.Scanner.GetTextOfNode(tag.AsJSDocThrowsTag().TypeExpression!));
                            if (comments is { Length: > 0 })
                            {
                                b.Append(' ');
                                WriteComments(b, c, comments, isMarkdown);
                            }
                        }
                        else if (comments is { Length: > 0 })
                        {
                            b.Append(' ');
                            if (comments[0].Kind != SyntaxKind.JSDocText || !comments[0].Text().StartsWith('-'))
                            {
                                b.Append("— ");
                            }
                            WriteComments(b, c, comments, isMarkdown);
                        }
                    }
                }
            }
        }
        return b.ToString();
    }

    // jsdoc.go:22-45 — GetSymbolDocumentationComment renders a symbol's documentation comment as
    // plain text (deduplicated across unique declarations, joined with line breaks).
    public string GetSymbolDocumentationComment(global::Ts.Checker.Checker c, Symbol? symbol)
    {
        if (symbol == null)
        {
            return "";
        }
        List<string>? parts = null;
        var seen = new HashSet<Node>();
        foreach (var decl in symbol.Declarations ?? [])
        {
            if (decl == null)
            {
                continue;
            }
            if (!seen.Add(decl))
            {
                continue;
            }
            var doc = GetDocumentationFromDeclaration(c, symbol, decl, decl, false /*isMarkdown*/, true /*commentOnly*/);
            if (doc.Length != 0 && (parts == null || !parts.Contains(doc)))
            {
                (parts ??= new List<string>()).Add(doc);
            }
        }
        return parts == null ? "" : string.Join("\n", parts);
    }

    // hover.go:245-250
    private static string FormatQuickInfo(string quickInfo)
    {
        var b = new StringBuilder(32);
        WriteCode(b, "typescript", quickInfo);
        return b.ToString();
    }

    // hover.go:252-265
    private static bool ShouldGetType(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                // If we're in a JSDoc node with no associated symbol, no binding has taken place for the node and
                // we can't answer questions about types of declaration nodes (such as property declarations).
                return !((node.Flags & NodeFlags.JSDoc) != 0 && AstUtilities.IsDeclarationName(node)) && !AstUtilities.IsLabelName(node) && !AstUtilities.IsTagName(node) && !AstUtilities.IsConstTypeReference(node.Parent!);
            case SyntaxKind.ThisKeyword:
            case SyntaxKind.ThisType:
            case SyntaxKind.SuperKeyword:
            case SyntaxKind.NamedTupleMember:
                return true;
            case SyntaxKind.MetaProperty:
                return AstUtilities.IsImportMeta(node);
            default:
                return false;
        }
    }

    // hover.go:267-271 — symbolDisplayInfo.
    private readonly record struct SymbolDisplayInfo(DisplayPartsWriter DisplayParts, Node? Declaration);

    // hover.go:273-775 — getQuickInfoAndDeclarationAtLocation builds classified display parts using
    // displayPartsWriter when vsCapability is true. When vsCapability is false, it still builds the
    // plain text string but skips classification runs.
    private SymbolDisplayInfo GetQuickInfoAndDeclarationAtLocation(
        global::Ts.Checker.Checker c, Symbol? symbol, Node node, global::Ts.Checker.VerbosityContext? vc, bool vsCapability, SemanticMeaning meaning)
    {
        var container = LsMeaning.GetContainerNode(node);
        vc ??= new global::Ts.Checker.VerbosityContext();
        var dpw = new DisplayPartsWriter(vsCapability);

        // PORT-NOTE: the vsCapability=true classified type/signature arms (NewNodeBuilderEx +
        // printer IdToSymbol, hover.go:293-351) are represented by their Go fallback branch — every
        // entry into this method passes vsCapability=false (matching Go's ProvideHover), so the
        // classified branch is unreachable until the VS-capability wave lands the classified printer.
        void WriteTypeClassified(global::Ts.Checker.Type t, Node? enclosing, global::Ts.Checker.TypeFormatFlags flags)
        {
            flags |= global::Ts.Checker.TypeFormatFlags.MultilineObjectLiterals;
            dpw.Write(c.TypeToStringEx(t, enclosing, flags, vc));
        }

        void WriteSignatureClassified(global::Ts.Checker.Signature sig, Node? enclosing, global::Ts.Checker.TypeFormatFlags flags)
        {
            flags |= global::Ts.Checker.TypeFormatFlags.MultilineObjectLiterals;
            dpw.Write(c.SignatureToStringEx(sig, enclosing, flags, vc));
        }

        void WriteSymbolClassified(Symbol? sym, Node? enclosing, SymbolFlags symMeaning, global::Ts.Checker.SymbolFormatFlags flags)
        {
            if (sym == null)
            {
                return;
            }
            if (!vsCapability)
            {
                dpw.Write(c.SymbolToStringEx(sym, enclosing, symMeaning, flags));
                return;
            }
            // Use WriteSymbol which calls classificationForSymbol to determine the correct classification
            var text = c.SymbolToStringEx(sym, enclosing, symMeaning, flags);
            dpw.WriteSymbol(text, sym);
        }

        if ((node.Kind == SyntaxKind.ThisKeyword && AstUtilities.IsInExpressionContext(node)) || AstUtilities.IsThisInTypeQuery(node))
        {
            dpw.WriteKeyword("this");
            dpw.WritePunctuation(": ");
            WriteTypeClassified(c.GetTypeAtLocation(node), container, HoverTypeFormatFlags);
            return new SymbolDisplayInfo(dpw, null);
        }
        if (symbol == null)
        {
            if (ShouldGetType(node))
            {
                WriteTypeClassified(c.GetTypeAtLocation(node), container, HoverTypeFormatFlags);
            }
            return new SymbolDisplayInfo(dpw, null);
        }
        var visitedAliases = new HashSet<Symbol>();
        var aliasLevel = 0;
        Node? firstDeclaration = null;
        void SetDeclaration(Node? declaration)
        {
            firstDeclaration ??= declaration;
        }
        void WriteNewLine()
        {
            if (dpw.String().Length != 0)
            {
                dpw.Write("\n");
            }
            if (aliasLevel != 0)
            {
                dpw.WritePunctuation("(");
                dpw.Write("alias");
                dpw.WritePunctuation(") ");
            }
        }
        void WriteSignatures(global::Ts.Checker.Signature[] signatures, string prefix, bool parenthesized, Symbol sym)
        {
            for (var i = 0; i < signatures.Length; i++)
            {
                var sig = signatures[i];
                WriteNewLine();
                if (i == 3 && signatures.Length >= 5)
                {
                    dpw.WriteComment($"// +{signatures.Length - 3} more overloads");
                    break;
                }
                if (parenthesized)
                {
                    dpw.WritePunctuation("(");
                    dpw.Write(prefix);
                    dpw.WritePunctuation(") ");
                }
                else
                {
                    dpw.WriteKeyword(prefix);
                }
                WriteSymbolClassified(sym, container, SymbolFlags.None, HoverSymbolFormatFlags);
                if ((sym.Flags & SymbolFlags.Optional) != 0)
                {
                    dpw.WritePunctuation("?");
                }
                WriteSignatureClassified(sig, container, HoverTypeFormatFlags | global::Ts.Checker.TypeFormatFlags.WriteCallStyleSignature | global::Ts.Checker.TypeFormatFlags.WriteTypeArgumentsOfSignature);
            }
        }
        void WriteTypeParams(global::Ts.Checker.Type[]? typeParams)
        {
            if (typeParams is { Length: > 0 })
            {
                dpw.WritePunctuation("<");
                for (var i = 0; i < typeParams.Length; i++)
                {
                    var tp = typeParams[i];
                    if (i != 0)
                    {
                        dpw.WritePunctuation(", ");
                    }
                    WriteSymbolClassified(tp.Symbol, null, SymbolFlags.None, HoverSymbolFormatFlags);
                    var cons = c.GetConstraintOfTypeParameter(tp);
                    if (cons != null)
                    {
                        dpw.WriteKeyword(" extends ");
                        WriteTypeClassified(cons, null, HoverTypeFormatFlags);
                    }
                    var def = c.GetDefaultFromTypeParameter(tp);
                    if (def != null)
                    {
                        dpw.WriteOperator(" = ");
                        WriteTypeClassified(def, null, HoverTypeFormatFlags);
                    }
                }
                dpw.WritePunctuation(">");
            }
        }
        var symbolWasExpanded = false;
        bool CanExpandSymbol(Symbol sym)
        {
            if (vc == null)
            {
                return false;
            }
            // Only offer symbol-level expansion for types that tryExpandSymbol handles:
            // class, interface, enum, namespace/module. For functions/variables/properties,
            // the node builder's probeTypeExpandability detects expandable type components.
            if ((sym.Flags & (SymbolFlags.Class | SymbolFlags.Interface | SymbolFlags.Namespace)) == 0)
            {
                return false;
            }
            global::Ts.Checker.Type? t;
            if ((sym.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) != 0)
            {
                t = c.GetDeclaredTypeOfSymbol(sym);
            }
            else
            {
                t = c.GetTypeOfSymbolAtLocation(sym, node);
            }
            if (t == null || c.IsLibTypeForHoverVerbosity(t))
            {
                return false;
            }
            if (vc.Level > 0)
            {
                return true;
            }
            // At level 0, signal that expansion is possible but don't expand
            vc.CanIncreaseVerbosity = true;
            return false;
        }
        // tryExpandSymbol checks if a symbol can be expanded at the current verbosity level.
        bool TryExpandSymbol(Symbol sym, SymbolFlags symMeaning)
        {
            if (symbolWasExpanded)
            {
                return true;
            }
            if (CanExpandSymbol(sym))
            {
                var expandVC = new global::Ts.Checker.VerbosityContext
                {
                    Level = vc!.Level - 1,
                    MaxTruncationLength = vc.MaxTruncationLength,
                };
                var expanded = c.ExpandSymbolForHover(sym, symMeaning, expandVC);
                if (expanded.Length != 0)
                {
                    vc.CanIncreaseVerbosity = vc.CanIncreaseVerbosity || expandVC.CanIncreaseVerbosity;
                    vc.Truncated = vc.Truncated || expandVC.Truncated;
                    dpw.Write(expanded);
                    symbolWasExpanded = true;
                    return true;
                }
            }
            return false;
        }
        Action<Symbol>? writeSymbolRef = null;
        void WriteSymbolCore(Symbol sym)
        {
            // Recursively write all meanings of alias
            if ((sym.Flags & SymbolFlags.Alias) != 0 && visitedAliases.Add(sym))
            {
                var aliasedSymbol = c.GetAliasedSymbol(sym);
                if (aliasedSymbol != null && aliasedSymbol != c.GetUnknownSymbol())
                {
                    aliasLevel++;
                    writeSymbolRef!(aliasedSymbol);
                    aliasLevel--;
                }
            }
            SymbolFlags flags;
            switch (meaning)
            {
                case SemanticMeaning.Value:
                    flags = sym.Flags & (SymbolFlags.Value | SymbolFlags.Signature);
                    break;
                case SemanticMeaning.Type:
                    flags = sym.Flags & SymbolFlags.Type;
                    break;
                case SemanticMeaning.Namespace:
                    flags = sym.Flags & SymbolFlags.Namespace;
                    break;
                default:
                    flags = sym.Flags & (SymbolFlags.Value | SymbolFlags.Signature | SymbolFlags.Type | SymbolFlags.Namespace);
                    break;
            }
            if (flags == 0)
            {
                if (aliasLevel != 0 || dpw.String().Length != 0)
                {
                    return;
                }
                flags = sym.Flags & (SymbolFlags.Value | SymbolFlags.Signature | SymbolFlags.Type | SymbolFlags.Namespace);
                if (flags == 0)
                {
                    return;
                }
            }
            if ((flags & SymbolFlags.Property) != 0 && sym.ValueDeclaration != null && NodeTests.IsMethodDeclaration(sym.ValueDeclaration))
            {
                flags = SymbolFlags.Method;
            }
            if ((flags & (SymbolFlags.Variable | SymbolFlags.Property | SymbolFlags.Accessor)) != 0)
            {
                WriteNewLine();
                if ((sym.CheckFlags & CheckFlags.IndexSymbol) == 0)
                {
                    if ((flags & SymbolFlags.Property) != 0)
                    {
                        dpw.WritePunctuation("(");
                        dpw.Write("property");
                        dpw.WritePunctuation(") ");
                    }
                    else if ((flags & SymbolFlags.Accessor) != 0)
                    {
                        dpw.WritePunctuation("(");
                        dpw.Write("accessor");
                        dpw.WritePunctuation(") ");
                    }
                    else
                    {
                        var decl = sym.ValueDeclaration;
                        if (decl != null)
                        {
                            decl = AstUtilities.GetRootDeclaration(decl);
                            if (NodeTests.IsParameterDeclaration(decl))
                            {
                                dpw.WritePunctuation("(");
                                dpw.Write("parameter");
                                dpw.WritePunctuation(") ");
                            }
                            else if (AstUtilities.IsVarLet(decl))
                            {
                                dpw.WriteKeyword("let ");
                            }
                            else if (AstUtilities.IsVarConst(decl))
                            {
                                dpw.WriteKeyword("const ");
                            }
                            else if (AstUtilities.IsVarUsing(decl))
                            {
                                dpw.WriteKeyword("using ");
                            }
                            else if (AstUtilities.IsVarAwaitUsing(decl))
                            {
                                dpw.WriteKeyword("await ");
                                dpw.WriteKeyword("using ");
                            }
                            else
                            {
                                dpw.WriteKeyword("var ");
                            }
                        }
                    }
                    if (sym.Name == InternalSymbolName.ExportEquals && sym.Parent != null && (sym.Parent.Flags & SymbolFlags.Module) != 0)
                    {
                        dpw.Write("exports");
                    }
                    else
                    {
                        WriteSymbolClassified(sym, container, SymbolFlags.None, HoverSymbolFormatFlags);
                    }
                    if ((sym.Flags & SymbolFlags.Optional) != 0)
                    {
                        dpw.WritePunctuation("?");
                    }
                    dpw.WritePunctuation(": ");
                }
                if (GetCallOrNewExpression(node) is { } callNode)
                {
                    var sigFlags = HoverTypeFormatFlags | global::Ts.Checker.TypeFormatFlags.WriteTypeArgumentsOfSignature | global::Ts.Checker.TypeFormatFlags.WriteArrowStyleSignature;
                    if (NodeTests.IsCallExpression(callNode))
                    {
                        sigFlags |= global::Ts.Checker.TypeFormatFlags.WriteCallStyleSignature;
                    }
                    WriteSignatureClassified(c.GetResolvedSignature(callNode)!, container, sigFlags);
                }
                else
                {
                    var t = c.GetTypeOfSymbolAtLocation(sym, node);
                    // If the type is a constrained type parameter, support expansion:
                    // Level 0: show just "T", signal canIncreaseVerbosity
                    // Level 1+: show "T extends Constraint" with the constraint expanded at level-1
                    if (vc != null && t.Symbol != null && (t.Symbol.Flags & SymbolFlags.TypeParameter) != 0 && c.GetConstraintOfTypeParameter(t) != null)
                    {
                        if (vc.Level > 0)
                        {
                            var expandVC = new global::Ts.Checker.VerbosityContext
                            {
                                Level = vc.Level - 1,
                                MaxTruncationLength = vc.MaxTruncationLength,
                            };
                            dpw.Write(c.TypeParameterToStringEx(t, container, expandVC));
                            vc.CanIncreaseVerbosity = vc.CanIncreaseVerbosity || expandVC.CanIncreaseVerbosity;
                            vc.Truncated = vc.Truncated || expandVC.Truncated;
                        }
                        else
                        {
                            WriteTypeClassified(t, container, HoverTypeFormatFlags);
                            vc.CanIncreaseVerbosity = true;
                        }
                    }
                    else
                    {
                        WriteTypeClassified(t, container, HoverTypeFormatFlags);
                    }
                }
                SetDeclaration(sym.ValueDeclaration);
            }
            if ((flags & SymbolFlags.EnumMember) != 0)
            {
                WriteNewLine();
                dpw.WritePunctuation("(");
                dpw.Write("enum member");
                dpw.WritePunctuation(") ");
                var t = c.GetTypeOfSymbol(sym);
                WriteTypeClassified(t, container, HoverTypeFormatFlags);
                if ((t.Flags & global::Ts.Checker.TypeFlags.Literal) != 0)
                {
                    dpw.WriteOperator(" = ");
                    dpw.WriteLiteral(c.ValueToString(t.AsLiteralType().Value()));
                }
                SetDeclaration(sym.ValueDeclaration);
            }
            if ((flags & (SymbolFlags.Function | SymbolFlags.Method)) != 0)
            {
                var isMethod = (flags & SymbolFlags.Method) != 0;
                var prefix = isMethod ? "method" : "function ";
                if (NodeTests.IsIdentifier(node) && (AstUtilities.IsFunctionLikeDeclaration(node.Parent!) || NodeTests.IsMethodSignatureDeclaration(node.Parent!)) && node.Parent!.Name() == node && (sym.Declarations ?? []).Contains(node.Parent))
                {
                    SetDeclaration(node.Parent);
                    var signatures = new[] { c.GetSignatureFromDeclaration(node.Parent!) };
                    WriteSignatures(signatures, prefix, isMethod, sym);
                }
                else
                {
                    var signatures = GetSignaturesAtLocation(c, sym, global::Ts.Checker.SignatureKind.Call, node);
                    if (signatures.Length == 1)
                    {
                        if (signatures[0].Declaration() is { } d && (d.Flags & NodeFlags.JSDoc) == 0)
                        {
                            SetDeclaration(d);
                        }
                    }
                    WriteSignatures(signatures, prefix, isMethod, sym);
                }
                SetDeclaration(sym.ValueDeclaration);
            }
            if ((flags & (SymbolFlags.Class | SymbolFlags.Interface)) != 0)
            {
                if (node.Kind == SyntaxKind.ThisKeyword || AstUtilities.IsThisInTypeQuery(node))
                {
                    WriteNewLine();
                    dpw.WriteKeyword("this");
                }
                else if (node.Kind == SyntaxKind.ConstructorKeyword && (NodeTests.IsConstructorDeclaration(node.Parent!) || NodeTests.IsConstructSignatureDeclaration(node.Parent!)))
                {
                    SetDeclaration(node.Parent);
                    var signatures = new[] { c.GetSignatureFromDeclaration(node.Parent!) };
                    WriteSignatures(signatures, "constructor ", false, sym);
                }
                else
                {
                    global::Ts.Checker.Signature[] signatures = [];
                    if ((flags & SymbolFlags.Class) != 0 && GetCallOrNewExpression(node) != null)
                    {
                        signatures = GetSignaturesAtLocation(c, sym, global::Ts.Checker.SignatureKind.Construct, node);
                    }
                    if (signatures.Length == 1)
                    {
                        if (signatures[0].Declaration() is { } d && (d.Flags & NodeFlags.JSDoc) == 0)
                        {
                            SetDeclaration(d);
                        }
                        WriteSignatures(signatures, "constructor ", false, sym);
                    }
                    else
                    {
                        WriteNewLine();
                        if ((flags & SymbolFlags.Class) != 0)
                        {
                            var classExpression = AstUtilities.GetDeclarationOfKind(sym, SyntaxKind.ClassExpression);
                            if (classExpression != null)
                            {
                                // Local class expression: show "(local class)" prefix
                                dpw.WritePunctuation("(");
                                dpw.Write("local class");
                                dpw.WritePunctuation(") ");
                            }
                            if (!TryExpandSymbol(sym, flags))
                            {
                                if (classExpression == null)
                                {
                                    if ((sym.Declarations ?? []).Any(d => NodeTests.IsClassDeclaration(d) && AstUtilities.HasAbstractModifier(d)))
                                    {
                                        dpw.WriteKeyword("abstract ");
                                    }
                                    dpw.WriteKeyword("class ");
                                }
                                WriteSymbolClassified(sym, container, SymbolFlags.None, HoverSymbolFormatFlags);
                                var typeParams = c.GetDeclaredTypeOfSymbol(sym).AsInterfaceType()?.LocalTypeParameters();
                                WriteTypeParams(typeParams);
                            }
                        }
                        else
                        {
                            if (!TryExpandSymbol(sym, flags))
                            {
                                dpw.WriteKeyword("interface ");
                                WriteSymbolClassified(sym, container, SymbolFlags.None, HoverSymbolFormatFlags);
                                var typeParams = c.GetDeclaredTypeOfSymbol(sym).AsInterfaceType()?.LocalTypeParameters();
                                WriteTypeParams(typeParams);
                            }
                        }
                    }
                }
                if ((flags & SymbolFlags.Class) != 0)
                {
                    SetDeclaration(sym.ValueDeclaration);
                }
                else
                {
                    SetDeclaration((sym.Declarations ?? []).FirstOrDefault(NodeTests.IsInterfaceDeclaration));
                }
            }
            if ((flags & SymbolFlags.Enum) != 0)
            {
                WriteNewLine();
                if (!TryExpandSymbol(sym, flags))
                {
                    if ((sym.Declarations ?? []).Any(d => NodeTests.IsEnumDeclaration(d) && AstUtilities.IsEnumConst(d)))
                    {
                        dpw.WriteKeyword("const ");
                    }
                    dpw.WriteKeyword("enum ");
                    WriteSymbolClassified(sym, container, SymbolFlags.None, HoverSymbolFormatFlags);
                }
                SetDeclaration((sym.Declarations ?? []).FirstOrDefault(NodeTests.IsEnumDeclaration));
            }
            if ((flags & SymbolFlags.Module) != 0)
            {
                WriteNewLine();
                if (!TryExpandSymbol(sym, flags))
                {
                    var isModule = sym.ValueDeclaration != null && (NodeTests.IsSourceFile(sym.ValueDeclaration) || AstUtilities.IsAmbientModule(sym.ValueDeclaration));
                    dpw.WriteKeyword(isModule ? "module " : "namespace ");
                    WriteSymbolClassified(sym, container, SymbolFlags.None, HoverSymbolFormatFlags);
                }
                SetDeclaration((sym.Declarations ?? []).FirstOrDefault(NodeTests.IsModuleDeclaration));
            }
            if ((flags & SymbolFlags.TypeParameter) != 0)
            {
                WriteNewLine();
                dpw.WritePunctuation("(");
                dpw.Write("type parameter");
                dpw.WritePunctuation(") ");
                var tp = c.GetDeclaredTypeOfSymbol(sym);
                WriteSymbolClassified(sym, container, SymbolFlags.None, HoverSymbolFormatFlags);
                var cons = c.GetConstraintOfTypeParameter(tp);
                if (cons != null)
                {
                    dpw.WriteKeyword(" extends ");
                    WriteTypeClassified(cons, container, HoverTypeFormatFlags);
                }
                // Show context: "in ClassName<T>" or "in funcName<T>(...)"
                if (sym.Parent != null)
                {
                    // Class/Interface type parameter
                    dpw.WriteKeyword(" in ");
                    WriteSymbolClassified(sym.Parent, container, SymbolFlags.None, HoverSymbolFormatFlags);
                    if (c.GetDeclaredTypeOfSymbol(sym.Parent).AsInterfaceType() is { } parentIface)
                    {
                        WriteTypeParams(parentIface.LocalTypeParameters());
                    }
                }
                else
                {
                    // Method/function type parameter
                    var decl = AstUtilities.GetDeclarationOfKind(sym, SyntaxKind.TypeParameter);
                    if (decl?.Parent != null)
                    {
                        var declaration = decl.Parent;
                        if (AstUtilities.IsFunctionLike(declaration))
                        {
                            dpw.WriteKeyword(" in ");
                            if (declaration.Kind == SyntaxKind.ConstructSignature)
                            {
                                dpw.WriteKeyword("new ");
                            }
                            else if (declaration.Kind != SyntaxKind.CallSignature && declaration.Name() != null)
                            {
                                WriteSymbolClassified(declaration.Symbol(), container, SymbolFlags.None, HoverSymbolFormatFlags);
                            }
                            var sig = c.GetSignatureFromDeclaration(declaration);
                            if (sig != null)
                            {
                                WriteSignatureClassified(sig, container, HoverTypeFormatFlags | global::Ts.Checker.TypeFormatFlags.WriteTypeArgumentsOfSignature);
                            }
                        }
                        else if (NodeTests.IsTypeAliasDeclaration(declaration))
                        {
                            dpw.WriteKeyword(" in ");
                            dpw.WriteKeyword("type ");
                            WriteSymbolClassified(declaration.Symbol(), container, SymbolFlags.None, HoverSymbolFormatFlags);
                            if (declaration.Symbol() is { } declSymbol)
                            {
                                WriteTypeParams(c.GetTypeAliasTypeParameters(declSymbol));
                            }
                        }
                    }
                }
                SetDeclaration((sym.Declarations ?? []).FirstOrDefault(NodeTests.IsTypeParameterDeclaration));
            }
            if ((flags & SymbolFlags.TypeAlias) != 0)
            {
                WriteNewLine();
                dpw.WriteKeyword("type ");
                WriteSymbolClassified(sym, container, SymbolFlags.None, HoverSymbolFormatFlags);
                WriteTypeParams(c.GetTypeAliasTypeParameters(sym));
                dpw.WriteOperator(" = ");
                global::Ts.Checker.Type typeAliasType;
                if (node.Parent != null && AstUtilities.IsConstTypeReference(node.Parent))
                {
                    typeAliasType = c.GetTypeAtLocation(node.Parent);
                }
                else
                {
                    typeAliasType = c.GetDeclaredTypeOfSymbol(sym);
                }
                WriteTypeClassified(typeAliasType, container, HoverTypeFormatFlags | global::Ts.Checker.TypeFormatFlags.InTypeAlias);
                SetDeclaration((sym.Declarations ?? []).FirstOrDefault(d => NodeTests.IsTypeAliasDeclaration(d) || d.Kind == SyntaxKind.JSTypeAliasDeclaration));
            }
            if ((flags & SymbolFlags.Signature) != 0)
            {
                WriteNewLine();
                WriteTypeClassified(c.GetTypeOfSymbol(sym), container, HoverTypeFormatFlags);
            }
        }
        writeSymbolRef = WriteSymbolCore;
        WriteSymbolCore(symbol);

        return new SymbolDisplayInfo(dpw, firstDeclaration);
    }

    // hover.go:782-799
    private static Node GetNodeForQuickInfo(Node node)
    {
        if (node.Parent is null)
        {
            return node;
        }
        if (NodeTests.IsNewExpression(node.Parent) && node.Pos == node.Parent.Pos)
        {
            return node.Parent.Expression()!;
        }
        if (NodeTests.IsNamedTupleMember(node.Parent) && node.Pos == node.Parent.Pos)
        {
            return node.Parent;
        }
        if (AstUtilities.IsImportMeta(node.Parent) && node.Parent.Name() == node)
        {
            return node.Parent;
        }
        if (NodeTests.IsJsxNamespacedName(node.Parent))
        {
            return node.Parent;
        }
        return node;
    }

    // hover.go:801-810
    private static Symbol? GetSymbolAtLocationForQuickInfo(global::Ts.Checker.Checker c, Node node)
    {
        if (LsMeaning.GetContainingObjectLiteralElement(node) is { } objectElement)
        {
            if (c.GetContextualType(objectElement.Parent!, global::Ts.Checker.ContextFlags.None) is { } contextualType)
            {
                var properties = c.GetPropertySymbolsFromContextualType(objectElement, contextualType, false /*unionSymbolOk*/);
                if (properties.Length == 1)
                {
                    return properties[0];
                }
            }
        }
        return c.GetSymbolAtLocation(node);
    }

    // hover.go:812-821
    private static global::Ts.Checker.Signature[] GetSignaturesAtLocation(global::Ts.Checker.Checker c, Symbol symbol, global::Ts.Checker.SignatureKind kind, Node node)
    {
        var signatures = c.GetSignaturesOfType(c.RemoveMissingOrUndefinedType(c.GetTypeOfSymbol(symbol)), kind);
        if (signatures.Length > 1 || (signatures.Length == 1 && signatures[0].TypeParameters() is { Length: > 0 }))
        {
            if (GetCallOrNewExpression(node) is { } callNode)
            {
                // We have a call or new expression, return the resolved signature
                return [c.GetResolvedSignature(callNode)!];
            }
        }
        return signatures;
    }

    // hover.go:823-834
    private static Node? GetCallOrNewExpression(Node node)
    {
        if (NodeTests.IsSourceFile(node))
        {
            return null;
        }
        if (NodeTests.IsPropertyAccessExpression(node.Parent!) && node.Parent!.Name() == node)
        {
            node = node.Parent;
        }
        if ((NodeTests.IsCallExpression(node.Parent!) || NodeTests.IsNewExpression(node.Parent!)) && node.Parent!.Expression() == node)
        {
            return node.Parent;
        }
        return null;
    }

    // hover.go:836-847
    private static bool ContainsTypedefTag(Node jsdoc)
    {
        if (jsdoc.Kind == SyntaxKind.JSDoc)
        {
            if (jsdoc.AsJSDoc().Tags is { } tags)
            {
                foreach (var tag in tags.Nodes)
                {
                    if (tag.Kind is SyntaxKind.JSDocTypedefTag or SyntaxKind.JSDocCallbackTag)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    // hover.go:849-851
    private static Node? GetJSDoc(Node node)
    {
        var jsdocs = node.JSDoc(null);
        return jsdocs is { Length: > 0 } ? jsdocs[^1] : null;
    }

    // hover.go:853-919
    private Node? GetJSDocOrTag(global::Ts.Checker.Checker c, Node node)
    {
        if (GetJSDoc(node) is { } jsdoc)
        {
            return jsdoc;
        }
        if (NodeTests.IsParameterDeclaration(node))
        {
            var name = node.Name()!;
            if (AstUtilities.IsBindingPattern(name))
            {
                // For binding patterns, match JSDoc @param tags by position rather than by name
                return GetJSDocParameterTagByPosition(c, node);
            }
            return GetMatchingJSDocTag(c, node.Parent!, name.Text(), IsMatchingParameterTag);
        }
        if (NodeTests.IsTypeParameterDeclaration(node))
        {
            return GetMatchingJSDocTag(c, node.Parent!, node.Name()!.Text(), IsMatchingTemplateTag);
        }
        if (NodeTests.IsVariableDeclaration(node) && NodeTests.IsVariableDeclarationList(node.Parent!) &&
            node.Parent!.AsVariableDeclarationList().Declarations!.Nodes.FirstOrDefault() == node)
        {
            return GetJSDocOrTag(c, node.Parent!.Parent!);
        }
        if ((AstUtilities.IsFunctionExpressionOrArrowFunction(node) || NodeTests.IsClassExpression(node)) &&
            (NodeTests.IsVariableDeclaration(node.Parent!) || NodeTests.IsPropertyDeclaration(node.Parent!) || NodeTests.IsPropertyAssignment(node.Parent!)) && node.Parent!.Initializer() == node)
        {
            return GetJSDocOrTag(c, node.Parent!);
        }
        if (NodeTests.IsBindingElement(node) && NodeTests.IsObjectBindingPattern(node.Parent!))
        {
            var name = node.PropertyNameOrName();
            if (name != null && NodeTests.IsIdentifier(name))
            {
                if (c.GetTypeAtLocation(node.Parent!) is { } objectType)
                {
                    if (c.GetPropertyOfType(objectType, name.Text()) is { } prop)
                    {
                        foreach (var d in prop.Declarations ?? [])
                        {
                            if (GetJSDoc(d) is { } propJsdoc)
                            {
                                return propJsdoc;
                            }
                        }
                    }
                }
            }
        }
        if (node.Symbol() is { } symbol && node.Parent != null)
        {
            if (NodeTests.IsFunctionDeclaration(node) || NodeTests.IsMethodDeclaration(node) || NodeTests.IsMethodSignatureDeclaration(node) || NodeTests.IsConstructorDeclaration(node) || NodeTests.IsConstructSignatureDeclaration(node))
            {
                var firstSignature = (symbol.Declarations ?? []).FirstOrDefault(AstUtilities.IsFunctionLike);
                if (firstSignature != null && node != firstSignature)
                {
                    if (GetJSDocOrTag(c, firstSignature) is { } sigJsDoc)
                    {
                        return sigJsDoc;
                    }
                }
            }
            if (AstUtilities.IsClassOrInterfaceLike(node.Parent))
            {
                var isStatic = AstUtilities.HasStaticModifier(node);
                var classType = c.GetDeclaredTypeOfSymbol(node.Parent.Symbol()!);
                if (isStatic)
                {
                    // For static members, use the checker's base constructor type resolution.
                    // This correctly handles intersection constructor types from mixins
                    // (e.g., typeof MixinClass & T) by preserving the full intersection.
                    var staticBaseType = c.GetApparentType(c.GetBaseConstructorTypeOfClass(classType));
                    if (c.GetPropertyOfType(staticBaseType, symbol.Name) is { ValueDeclaration: { } vd })
                    {
                        if (GetJSDocOrTag(c, vd) is { } baseJsDoc)
                        {
                            return baseJsDoc;
                        }
                    }
                }
                else
                {
                    foreach (var baseType in c.GetBaseTypes(classType))
                    {
                        if (c.GetPropertyOfType(baseType, symbol.Name) is { ValueDeclaration: { } vd })
                        {
                            if (GetJSDocOrTag(c, vd) is { } baseJsDoc)
                            {
                                return baseJsDoc;
                            }
                        }
                    }
                }
            }
        }
        return null;
    }

    // hover.go:921-932
    private Node? GetMatchingJSDocTag(global::Ts.Checker.Checker c, Node node, string name, Func<Node, string, bool> match)
    {
        if (GetJSDocOrTag(c, node) is { } jsdoc && jsdoc.Kind == SyntaxKind.JSDoc)
        {
            if (jsdoc.AsJSDoc().Tags is { } tags)
            {
                foreach (var tag in tags.Nodes)
                {
                    if (match(tag, name))
                    {
                        return tag;
                    }
                }
            }
        }
        return null;
    }

    // hover.go:934-977 — getJSDocParameterTagByPosition finds a JSDoc @param tag for a binding
    // pattern parameter by position. Since binding patterns don't have a simple name, we match the
    // @param tag at the same index as the parameter.
    private Node? GetJSDocParameterTagByPosition(global::Ts.Checker.Checker c, Node param)
    {
        var parent = param.Parent;
        if (parent == null)
        {
            return null;
        }

        // Find the parameter's index in the parent's parameters list
        var parameters = parent.Parameters() ?? [];
        var paramIndex = -1;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i] == param)
            {
                paramIndex = i;
                break;
            }
        }
        if (paramIndex < 0)
        {
            return null;
        }

        // Get the JSDoc for the parent function/method
        var jsdoc = GetJSDocOrTag(c, parent);
        if (jsdoc == null || jsdoc.Kind != SyntaxKind.JSDoc)
        {
            return null;
        }

        // Collect all @param tags in order
        var tags = jsdoc.AsJSDoc().Tags;
        if (tags == null)
        {
            return null;
        }

        var paramTagIndex = 0;
        foreach (var tag in tags.Nodes)
        {
            if (tag.Kind == SyntaxKind.JSDocParameterTag)
            {
                if (paramTagIndex == paramIndex)
                {
                    return tag;
                }
                paramTagIndex++;
            }
        }
        return null;
    }

    // hover.go:979-990
    private static bool IsMatchingParameterTag(Node tag, string name) =>
        tag.Kind == SyntaxKind.JSDocParameterTag && IsNodeWithName(tag, name);

    private static bool IsMatchingTemplateTag(Node tag, string name) =>
        tag.Kind == SyntaxKind.JSDocTemplateTag && (tag.TypeParameters() ?? []).Any(tp => IsNodeWithName(tp, name));

    private static bool IsNodeWithName(Node node, string name)
    {
        var nodeName = node.Name();
        return nodeName != null && NodeTests.IsIdentifier(nodeName) && nodeName.Text() == name;
    }

    // hover.go:992-1011
    private static void WriteCode(StringBuilder b, string lang, string code)
    {
        if (code.Length == 0)
        {
            return;
        }
        var ticks = 3;
        while (code.Contains(new string('`', ticks), StringComparison.Ordinal))
        {
            ticks++;
        }
        b.Append('`', ticks);
        b.Append(lang);
        b.Append('\n');
        b.Append(code);
        b.Append('\n');
        b.Append('`', ticks);
        b.Append('\n');
    }

    // hover.go:1013-1024
    internal void WriteComments(StringBuilder b, global::Ts.Checker.Checker c, Node[]? comments, bool isMarkdown)
    {
        foreach (var comment in comments ?? [])
        {
            switch (comment.Kind)
            {
                case SyntaxKind.JSDocText:
                    b.Append(comment.Text());
                    break;
                case SyntaxKind.JSDocLink:
                case SyntaxKind.JSDocLinkPlain:
                    WriteJSDocLink(b, c, comment, false /*quote*/, isMarkdown);
                    break;
                case SyntaxKind.JSDocLinkCode:
                    WriteJSDocLink(b, c, comment, true /*quote*/, isMarkdown);
                    break;
            }
        }
    }

    // hover.go:1026-1056
    private void WriteJSDocLink(StringBuilder b, global::Ts.Checker.Checker c, Node link, bool quote, bool isMarkdown)
    {
        var name = link.Name();
        var text = link.Text().Trim(' ');
        if (name == null)
        {
            WriteQuotedString(b, text, quote && isMarkdown);
            return;
        }
        if (NodeTests.IsIdentifier(name) && (name.Text() == "http" || name.Text() == "https") && text.StartsWith("://", StringComparison.Ordinal))
        {
            var linkText = name.Text() + text;
            var linkUri = linkText;
            var commentPos = linkText.AsSpan().IndexOfAny(' ', '|');
            if (commentPos >= 0)
            {
                linkUri = linkText[..commentPos];
                linkText = TrimCommentPrefix(linkText[commentPos..]);
                if (linkText.Length == 0)
                {
                    linkText = linkUri;
                }
            }
            if (isMarkdown)
            {
                WriteMarkdownLink(b, linkText, linkUri, quote);
            }
            else
            {
                WriteQuotedString(b, linkText, false);
                if (linkText != linkUri)
                {
                    b.Append(" (");
                    b.Append(linkUri);
                    b.Append(')');
                }
            }
            return;
        }
        WriteNameLink(b, c, name, text, quote, isMarkdown);
    }

    // hover.go:1058-1079
    // PORT-NOTE: the markdown declaration-link arm needs getDeclarationsFromLocation +
    // getMappedLocation (definition.go cluster); until that lands, both formats take the Go
    // no-declarations fallback (quoted entity name + text), which is identical for the plaintext
    // hover path the C# server drives.
    internal void WriteNameLink(StringBuilder b, global::Ts.Checker.Checker c, Node name, string text, bool quote, bool isMarkdown)
    {
        WriteQuotedString(b, GetEntityNameString(name) + (text.Length != 0 ? " " : "") + text, quote && isMarkdown);
    }

    // hover.go:1081-1083
    private static string TrimCommentPrefix(string text)
    {
        var s = text.TrimStart(' ');
        if (s.StartsWith('|'))
        {
            s = s[1..];
        }
        return s.TrimStart(' ');
    }

    // hover.go:1085-1091
    private static void WriteMarkdownLink(StringBuilder b, string text, string uri, bool quote)
    {
        b.Append('[');
        WriteQuotedString(b, text, quote);
        b.Append("](");
        b.Append(uri);
        b.Append(')');
    }

    // hover.go:1093-1098
    private static void WriteOptionalEntityName(StringBuilder b, Node? name)
    {
        if (name != null)
        {
            b.Append(' ');
            WriteQuotedString(b, GetEntityNameString(name), true /*quote*/);
        }
    }

    // hover.go:1100-1108
    private static void WriteQuotedString(StringBuilder b, string str, bool quote)
    {
        if (quote && !str.Contains('`'))
        {
            b.Append('`');
            b.Append(str);
            b.Append('`');
        }
        else
        {
            b.Append(str);
        }
    }

    // hover.go:1110-1114
    private static string GetEntityNameString(Node name)
    {
        var b = new StringBuilder();
        WriteEntityNameParts(b, name);
        return b.ToString();
    }

    // hover.go:1116-1133
    private static void WriteEntityNameParts(StringBuilder b, Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                b.Append(node.Text());
                break;
            case SyntaxKind.QualifiedName:
                WriteEntityNameParts(b, node.AsQualifiedName().Left!);
                b.Append('.');
                WriteEntityNameParts(b, node.AsQualifiedName().Right!);
                break;
            case SyntaxKind.PropertyAccessExpression:
                WriteEntityNameParts(b, node.Expression()!);
                b.Append('.');
                WriteEntityNameParts(b, node.Name()!);
                break;
            case SyntaxKind.ParenthesizedExpression:
            case SyntaxKind.ExpressionWithTypeArguments:
                WriteEntityNameParts(b, node.Expression()!);
                break;
            case SyntaxKind.JSDocNameReference:
                WriteEntityNameParts(b, node.Name()!);
                break;
        }
    }
}
