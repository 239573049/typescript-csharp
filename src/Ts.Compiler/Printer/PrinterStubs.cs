// Stubs for references from the printer package (factory.go/helpers.go wave) into functionality that has
// not yet been ported. Each stub throws NotImplementedException naming the Go symbol it stands in for, so a
// hit at runtime is obvious and the remaining work is discoverable via grep.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Printer;

// PORT-NOTE: Stubs for the remaining internal/printer/utilities.go functions that printer.go references but
// that are not part of this emit-body wave (utilities.go is a later wave). Each throws NotImplementedException
// naming the Go symbol. These are only reached on the comment-recognition and `preserveSourceNewlines`
// emit paths (isJSDocLikeText / IsPinnedComment / IsRecognizedTripleSlashComment / calculateIndent /
// siblingNodePositionsAreComparable / originalNodesHaveSameParent) and on unique-name generation with a
// currentSourceFile (IsFileLevelUniqueName). Port them with the rest of utilities.go and remove these.
internal static partial class PrinterUtilities
{
    // Go: IsFileLevelUniqueName (utilities.go:651)
    internal static bool IsFileLevelUniqueName(SourceFile sourceFile, string name, Func<string, bool>? hasGlobalName)
    {
        throw new NotImplementedException("IsFileLevelUniqueName: internal/printer/utilities.go, not yet ported");
    }

    // Go: IsRecognizedTripleSlashComment (utilities.go:804)
    internal static bool IsRecognizedTripleSlashComment(string text, CommentRange commentRange)
    {
        throw new NotImplementedException("IsRecognizedTripleSlashComment: internal/printer/utilities.go, not yet ported");
    }

    // Go: siblingNodePositionsAreComparable (utilities.go:422)
    internal static bool SiblingNodePositionsAreComparable(EmitContext emitContext, Node previousNode, Node nextNode)
    {
        throw new NotImplementedException("siblingNodePositionsAreComparable: internal/printer/utilities.go, not yet ported");
    }
}

internal static class PrinterStubs
{
    // Go: ast.TryGetPropertyNameOfBindingOrAssignmentElement (internal/ast/utilities.go:3900).
    //
    // Called by NodeFactory.NewRestHelper. Not yet ported to the C# ast package: its fallback branch depends
    // on ast.GetTargetOfBindingOrAssignmentElement, which is also unported. Porting it would mean pulling an
    // unrelated slice of ast/utilities.go into this printer wave, so it is stubbed here rather than
    // partially/inconsistently ported. Relocate to Ts.Ast.AstUtilities when that ast helper chain lands.
    internal static Node? TryGetPropertyNameOfBindingOrAssignmentElement(Node bindingElement)
    {
        throw new NotImplementedException("TryGetPropertyNameOfBindingOrAssignmentElement: requires ast.GetTargetOfBindingOrAssignmentElement (internal/ast/utilities.go), not yet ported");
    }
}
