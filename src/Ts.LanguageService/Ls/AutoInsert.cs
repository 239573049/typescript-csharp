// Ported from tsgo internal/ls/autoinsert.go (1:1).
//
// The Go entry point receives the raw LSP request (VSOnAutoInsertParams) and converts
// the line/character position through l.converters; here that conversion is
// LsDeps.LineAndCharacterToPosition (UTF-16 direct). Positions are UTF-16 code units
// throughout. Go's (VSOnAutoInsertResponse, error) pair collapses to the response
// union value (VSOnAutoInsertResponseItemOrNull); the zero response is the union with
// no item.

using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Core; // Tristate extension methods (IsTrue/IsFalse)

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // autoinsert.go:12-69 — ProvideOnAutoInsert.
    public Ts.LsProto.VSOnAutoInsertResponseItemOrNull ProvideOnAutoInsert(
        CancellationToken ctx,
        Ts.LsProto.VSOnAutoInsertParams request)
    {
        if (UserPreferences().EnableAutoClosingTags.IsFalse())
        {
            return new Ts.LsProto.VSOnAutoInsertResponseItemOrNull();
        }
        if (request.VSCh != ">")
        {
            return new Ts.LsProto.VSOnAutoInsertResponseItemOrNull();
        }

        var sourceFile = GetProgramAndFile(request.VSTextDocument.Uri);
        var position = LsDeps.LineAndCharacterToPosition(sourceFile, request.VSPosition.Line, request.VSPosition.Character);

        var token = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, position);
        if (token == null)
        {
            return new Ts.LsProto.VSOnAutoInsertResponseItemOrNull();
        }

        string? closingText = null;
        Node? element = null;
        if (token.Kind == SyntaxKind.GreaterThanToken && NodeTests.IsJsxOpeningElement(token.Parent!))
        {
            element = token.Parent!.Parent;
        }
        else if (NodeTests.IsJsxText(token) && NodeTests.IsJsxElement(token.Parent!))
        {
            element = token.Parent;
        }

        if (element != null && IsUnclosedTag(element.AsJsxElement()))
        {
            var tagNameNode = element.AsJsxElement().OpeningElement!.TagName();
            // Slight divergence from Strada - we don't use the verbatim text from the opening tag.
            closingText = "</" + AstUtilities.EntityNameToString(tagNameNode!, global::Ts.Scanner.Scanner.GetTextOfNode) + ">";
        }
        else
        {
            Node? fragment = null;
            if (token.Kind == SyntaxKind.GreaterThanToken && NodeTests.IsJsxOpeningFragment(token.Parent!))
            {
                fragment = token.Parent!.Parent;
            }
            else if (NodeTests.IsJsxText(token) && NodeTests.IsJsxFragment(token.Parent!))
            {
                fragment = token.Parent;
            }

            if (fragment != null && IsUnclosedFragment(fragment.AsJsxFragment()))
            {
                closingText = "</>";
            }
        }

        if (closingText == null)
        {
            return new Ts.LsProto.VSOnAutoInsertResponseItemOrNull();
        }

        return new Ts.LsProto.VSOnAutoInsertResponseItemOrNull
        {
            VSOnAutoInsertResponseItem = new Ts.LsProto.VSOnAutoInsertResponseItem
            {
                VSTextEditFormat = Ts.LsProto.InsertTextFormat.Snippet,
                VSTextEdit = new Ts.LsProto.TextEdit
                {
                    Range = new Ts.LsProto.Range
                    {
                        // Go copies the position struct into both ends; C# Position is a
                        // class, so the two ends get independent instances.
                        Start = new Ts.LsProto.Position { Line = request.VSPosition.Line, Character = request.VSPosition.Character },
                        End = new Ts.LsProto.Position { Line = request.VSPosition.Line, Character = request.VSPosition.Character },
                    },
                    // Tag names can contain `$` (valid JSX identifier characters), so
                    // escape the closing text to avoid being interpreted as a snippet
                    // placeholder/variable.
                    NewText = "$0" + EscapeSnippetText(closingText),
                },
            },
        };
    }

    // autoinsert.go:71-85 — isUnclosedTag.
    private static bool IsUnclosedTag(global::Ts.Ast.JsxElement node)
    {
        var openingElement = node.OpeningElement!;
        var closingElement = node.ClosingElement!;
        if (!global::Ts.Ast.JsxParserAstGaps.TagNamesAreEquivalent(openingElement.TagName()!, closingElement.TagName()!))
        {
            return true;
        }

        var parent = node.Parent!;
        if (NodeTests.IsJsxElement(parent))
        {
            var parentElement = parent.AsJsxElement();
            return global::Ts.Ast.JsxParserAstGaps.TagNamesAreEquivalent(openingElement.TagName()!, parentElement.OpeningElement!.TagName()!) && IsUnclosedTag(parentElement);
        }

        return false;
    }

    // autoinsert.go:87-99 — isUnclosedFragment.
    private static bool IsUnclosedFragment(global::Ts.Ast.JsxFragment node)
    {
        var closingFragment = node.ClosingFragment!;
        if ((closingFragment.Flags & NodeFlags.ThisNodeHasError) != 0)
        {
            return true;
        }

        var parent = node.Parent!;
        if (NodeTests.IsJsxFragment(parent) && IsUnclosedFragment(parent.AsJsxFragment()))
        {
            return true;
        }

        return false;
    }
}
