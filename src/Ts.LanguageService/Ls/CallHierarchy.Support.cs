// Support partial for Ls/CallHierarchy.cs (tsgo internal/ls/callhierarchy.go).
//
// Holds the pieces the 1:1 port needs that are not yet provided by the shared
// foundation files (LsDeps.cs / LanguageService.cs / Host.cs / Lsp/Protocol.cs):
//   1. lsproto wire types for the call-hierarchy requests (lsp_generated.go subset)
//      — Protocol.cs does not carry them yet.
//   2. The ast isCalleeWorker family (ast/utilities.go:3585-3677) used by
//      convertEntryToCallSite — not yet ported into Ts.Ast.
//   3. singlelinestringwriter.go — internal to Ts.Printer, re-hosted here for
//      getTextOfCallHierarchyName's printer fallback.
//   4. The foundation reference scan that stands in for
//      provideSymbolsAndEntries/getReferencedSymbolsForNode (findallreferences.go),
//      whose deep path is deferred with checker internals — see the PORT-NOTE at
//      SymbolAndEntriesToIncomingCalls in CallHierarchy.cs.

using System;
using System.Collections.Generic;
using System.Text;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods

namespace Ts.Ls
{

// ast/utilities.go:3585-3677 — the callee/target position tests, not yet ported into
// Ts.Ast; hosted here because convertEntryToCallSite is their only LS consumer.
internal static class CallHierarchyAstHelpers
{
    // ast/utilities.go:3585 — IsRightSideOfPropertyAccess.
    internal static bool IsRightSideOfPropertyAccess(Node node)
    {
        return node.Parent!.Kind == SyntaxKind.PropertyAccessExpression && node.Parent.Name() == node;
    }

    // ast/utilities.go:3589 — IsArgumentExpressionOfElementAccess.
    internal static bool IsArgumentExpressionOfElementAccess(Node node)
    {
        return node.Parent != null && node.Parent.Kind == SyntaxKind.ElementAccessExpression && node.Parent.AsElementAccessExpression().ArgumentExpression == node;
    }

    // ast/utilities.go:3601 — ClimbPastPropertyAccess.
    internal static Node ClimbPastPropertyAccess(Node node)
    {
        if (IsRightSideOfPropertyAccess(node))
        {
            return node.Parent!;
        }
        return node;
    }

    // ast/utilities.go:3606 — climbPastPropertyOrElementAccess.
    internal static Node ClimbPastPropertyOrElementAccess(Node node)
    {
        if (IsRightSideOfPropertyAccess(node) || IsArgumentExpressionOfElementAccess(node))
        {
            return node.Parent!;
        }
        return node;
    }

    // ast/utilities.go:3616 — selectExpressionOfCallOrNewExpressionOrDecorator.
    internal static Node? SelectExpressionOfCallOrNewExpressionOrDecorator(Node node)
    {
        if (NodeTests.IsCallExpression(node) || NodeTests.IsNewExpression(node) || NodeTests.IsDecorator(node))
        {
            return node.Expression();
        }
        return null;
    }

    // ast/utilities.go:3624 — selectTagOfTaggedTemplateExpression.
    internal static Node? SelectTagOfTaggedTemplateExpression(Node node)
    {
        if (NodeTests.IsTaggedTemplateExpression(node))
        {
            return node.AsTaggedTemplateExpression().Tag;
        }
        return null;
    }

    // ast/utilities.go:3630 — selectTagNameOfJsxOpeningLikeElement.
    internal static Node? SelectTagNameOfJsxOpeningLikeElement(Node node)
    {
        if (NodeTests.IsJsxOpeningElement(node) || NodeTests.IsJsxSelfClosingElement(node))
        {
            return node.TagName();
        }
        return null;
    }

    // ast/utilities.go:3636 — IsCallOrNewExpressionTarget.
    internal static bool IsCallOrNewExpressionTarget(Node node, bool includeElementAccess, bool skipPastOuterExpressions)
    {
        return IsCalleeWorker(node, AstUtilities.IsCallOrNewExpression, SelectExpressionOfCallOrNewExpressionOrDecorator, includeElementAccess, skipPastOuterExpressions);
    }

    // ast/utilities.go:3640 — IsTaggedTemplateTag.
    internal static bool IsTaggedTemplateTag(Node node, bool includeElementAccess, bool skipPastOuterExpressions)
    {
        return IsCalleeWorker(node, NodeTests.IsTaggedTemplateExpression, SelectTagOfTaggedTemplateExpression, includeElementAccess, skipPastOuterExpressions);
    }

    // ast/utilities.go:3644 — IsDecoratorTarget.
    internal static bool IsDecoratorTarget(Node node, bool includeElementAccess, bool skipPastOuterExpressions)
    {
        return IsCalleeWorker(node, NodeTests.IsDecorator, SelectExpressionOfCallOrNewExpressionOrDecorator, includeElementAccess, skipPastOuterExpressions);
    }

    // ast/utilities.go:3648 — IsJsxOpeningLikeElementTagName.
    internal static bool IsJsxOpeningLikeElementTagName(Node node, bool includeElementAccess, bool skipPastOuterExpressions)
    {
        return IsCalleeWorker(node, AstUtilities.IsJsxOpeningLikeElement, SelectTagNameOfJsxOpeningLikeElement, includeElementAccess, skipPastOuterExpressions);
    }

    // ast/utilities.go:3653 — isCalleeWorker.
    private static bool IsCalleeWorker(
        Node node,
        Func<Node, bool> pred,
        Func<Node, Node?> calleeSelector,
        bool includeElementAccess,
        bool skipPastOuterExpressions)
    {
        Node target;
        if (includeElementAccess)
        {
            target = ClimbPastPropertyOrElementAccess(node);
        }
        else
        {
            target = ClimbPastPropertyAccess(node);
        }
        if (skipPastOuterExpressions)
        {
            // Only skip outer expressions if the target is actually an expression node
            if (AstUtilities.IsExpression(target))
            {
                target = AstUtilities.SkipOuterExpressions(target, OuterExpressionKinds.All);
            }
        }
        return target.Parent != null && pred(target.Parent) && calleeSelector(target.Parent) == target;
    }
}

// lsp/lsproto/lsp.go:19 — DocumentUri.FileName. The shared Protocol.cs DocumentUri record
// carries only the raw Value (the lsconv reverse codec is deferred with the project layer),
// so the file:// subset that LsDeps.FileNameToDocumentURI produces is inverted here. Go's
// bundled:// short-circuit, non-file-scheme round-trip, "//host" and fixWindowsURIPath arms
// are deferred exactly like the forward direction's.
internal static class CallHierarchyUriExtensions
{
    public static string FileName(this global::Ts.LsProto.DocumentUri uri)
    {
        var value = uri.Value;
        if (!value.StartsWith("file://", System.StringComparison.Ordinal))
        {
            // Go panics on a non-file URI; the repository maps panic to
            // InvalidOperationException.
            throw new InvalidOperationException("invalid URI: " + value);
        }
        var path = value["file://".Length..];
        var parts = path.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = System.Uri.UnescapeDataString(parts[i]);
        }
        return string.Join("/", parts);
    }
}

// printer/singlelinestringwriter.go — internal to Ts.Printer; re-hosted with the same
// behavior for getTextOfCallHierarchyName's printer fallback (per Go semantics: the
// pooled writer is not in plan/01 §5.2's named pool list, so `new` it).
internal sealed class SingleLineTextWriter : global::Ts.Printer.EmitTextWriter
{
    private readonly StringBuilder builder = new();
    private string lastWritten = "";

    public void Clear()
    {
        lastWritten = "";
        builder.Clear();
    }

    public void DecreaseIndent()
    {
        // Do Nothing
    }

    public int GetColumn() => 0;

    public int GetIndent() => 0;

    public int GetLine() => 0;

    public string String() => builder.ToString();

    public int GetTextPos() => builder.Length;

    public bool HasTrailingComment() => false;

    public bool HasTrailingWhitespace()
    {
        if (builder.Length == 0)
        {
            return false;
        }
        var ch = lastWritten.Length > 0 ? lastWritten[^1] : default;
        return ch is ' ' or '\t' or '\n' or '\r' or '\v' or '\f';
    }

    public void IncreaseIndent()
    {
        // Do Nothing
    }

    public bool IsAtStartOfLine() => false;

    public void RawWrite(string s)
    {
        lastWritten = s;
        builder.Append(s);
    }

    public void Write(string s)
    {
        lastWritten = s;
        builder.Append(s);
    }

    public void WriteComment(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteKeyword(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    // singlelinestringwriter.go — WriteLine collapses to a single space.
    public void WriteLine()
    {
        lastWritten = " ";
        builder.Append(' ');
    }

    public void WriteLineForce(bool force)
    {
        lastWritten = " ";
        builder.Append(' ');
    }

    public void WriteLiteral(string s)
    {
        lastWritten = s;
        builder.Append(s);
    }

    public void WriteOperator(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteParameter(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteProperty(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WritePunctuation(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteSpace(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteStringLiteral(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteSymbol(string text, Symbol symbol)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteTrailingSemicolon(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }
}
}
