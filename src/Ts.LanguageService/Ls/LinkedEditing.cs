// Ported from tsgo internal/ls/linkedediting.go.
//
// PORT-NOTE: the Go entry point takes an LSP line/character position converted
// through l.converters; the C# LS foundation's position currency is the raw UTF-16
// offset (see LanguageService.GetNodeAtPosition), so the entry point takes the
// offset directly. The Go LinkedEditingRangeResponse zero value (nil ranges) maps
// to a null return. Positions are UTF-16 code units throughout.

using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using LinkedEditingRanges = Ts.LsProto.LinkedEditingRanges;
using LspRange = Ts.LsProto.Range;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // linkedediting.go:15 — jsxTagWordPattern.
    // allow the client to match more than valid tag names. This allows linked editing when typing is in progress or tag name is incomplete
    private const string JsxTagWordPattern = "[a-zA-Z0-9:\\-\\._$]*";

    // linkedediting.go:17 — ProvideLinkedEditingRange.
    public LinkedEditingRanges? ProvideLinkedEditingRange(string fileName, int position)
    {
        var sourceFile = GetProgramAndFile(fileName);
        var token = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, position);

        if (token == null || token.Parent!.Kind == SyntaxKind.SourceFile) // Go dereferences Parent unconditionally.
        {
            return null;
        }

        if (NodeTests.IsJsxFragment(token.Parent!.Parent!)) // Go dereferences Parent.Parent unconditionally.
        {
            var fragment = token.Parent!.Parent!.AsJsxFragment();
            var openFragment = fragment.OpeningFragment!; // Go dereferences unconditionally.
            var closeFragment = fragment.ClosingFragment!; // Go dereferences unconditionally.
            if ((openFragment.Flags & NodeFlags.ThisNodeOrAnySubNodesHasError) != 0 ||
                (closeFragment.Flags & NodeFlags.ThisNodeOrAnySubNodesHasError) != 0)
            {
                return null;
            }

            var openPos = global::Ts.AstNav.Tokens.GetStartOfNode(openFragment, sourceFile, false) + "<".Length;
            var closePos = global::Ts.AstNav.Tokens.GetStartOfNode(closeFragment, sourceFile, false) + "</".Length;

            // only allows linked editing right after opening bracket: <| ></| >
            if (position != openPos && position != closePos)
            {
                return null;
            }

            var openLineChar = LsDeps.PositionToLineAndCharacter(sourceFile, openPos);
            var closeLineChar = LsDeps.PositionToLineAndCharacter(sourceFile, closePos);
            return new LinkedEditingRanges
            {
                Ranges =
                [
                    new LspRange { Start = openLineChar, End = openLineChar }, // only return start position for opening tag since the length of a fragment is always 3 and it is unlikely user will type in the middle of a fragment tag
                    new LspRange { Start = closeLineChar, End = closeLineChar },
                ],
                WordPattern = JsxTagWordPattern,
            };
        }
        else
        {
            // determines if the cursor is in an element tag
            var tag = AstUtilities.FindAncestor(token.Parent, n =>
            {
                if (NodeTests.IsJsxOpeningElement(n) || NodeTests.IsJsxClosingElement(n))
                {
                    return true;
                }
                return false;
            });
            if (tag == null)
            {
                return null;
            }
            global::Ts.Core.Debug.Assert(
                NodeTests.IsJsxOpeningElement(tag) || NodeTests.IsJsxClosingElement(tag),
                "tag should be opening or closing element");

            var jsxElement = tag.Parent!.AsJsxElement(); // Go dereferences Parent unconditionally.
            var openTag = jsxElement.OpeningElement!; // Go dereferences unconditionally.
            var closeTag = jsxElement.ClosingElement!; // Go dereferences unconditionally.

            var openTagNameStart = global::Ts.AstNav.Tokens.GetStartOfNode(openTag.TagName()!, sourceFile, false);
            var openTagNameEnd = openTag.TagName()!.End;
            var closeTagNameStart = global::Ts.AstNav.Tokens.GetStartOfNode(closeTag.TagName()!, sourceFile, false);
            var closeTagNameEnd = closeTag.TagName()!.End;
            // do not return linked cursors if tags are not well-formed
            if (openTagNameStart == global::Ts.AstNav.Tokens.GetStartOfNode(openTag, sourceFile, false) ||
                closeTagNameStart == global::Ts.AstNav.Tokens.GetStartOfNode(closeTag, sourceFile, false) ||
                openTagNameEnd == openTag.End || closeTagNameEnd == closeTag.End)
            {
                return null;
            }
            // only return linked cursors if the cursor is within a tag name
            if (!(openTagNameStart <= position && position <= openTagNameEnd ||
                closeTagNameStart <= position && position <= closeTagNameEnd))
            {
                return null;
            }

            // only return linked cursors if text in both tags is identical
            var openingTagText = global::Ts.Scanner.Scanner.GetTextOfNode(openTag.TagName()!);
            if (openingTagText != global::Ts.Scanner.Scanner.GetTextOfNode(closeTag.TagName()!))
            {
                return null;
            }

            return new LinkedEditingRanges
            {
                Ranges =
                [
                    new LspRange
                    {
                        Start = LsDeps.PositionToLineAndCharacter(sourceFile, openTagNameStart),
                        End = LsDeps.PositionToLineAndCharacter(sourceFile, openTagNameEnd),
                    },
                    new LspRange
                    {
                        Start = LsDeps.PositionToLineAndCharacter(sourceFile, closeTagNameStart),
                        End = LsDeps.PositionToLineAndCharacter(sourceFile, closeTagNameEnd),
                    },
                ],
                WordPattern = JsxTagWordPattern,
            };
        }
    }
}
