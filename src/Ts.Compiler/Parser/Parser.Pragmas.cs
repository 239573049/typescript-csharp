// Ported from tsgo internal/parser/parser.go (pragma scanning section).
// PORT-NOTE / DEVIATION (AGENTS §6): getCommentPragmas/extractPragmas/processPragmasIntoFields
// (triple-slash <reference>, amd-module, ts-nocheck, jsx pragmas, etc.) are not yet ported.
// They populate SourceFile.Pragmas / ReferencedFiles / CheckJsDirective etc., consumed by the
// program/checker in later phases — not by the parse-tree structure that phase-3 gates on.
// TODO(phase-4): port the pragma spec tables (CommentPragmas) and field extraction faithfully.
using Ts.Ast;

namespace Ts.Parser;

public sealed partial class Parser
{
    private Pragma[]? GetCommentPragmas(NodeFactory f, string sourceText)
    {
        // !!! pragma scanning pending (see PORT-NOTE)
        return null;
    }

    private void ProcessPragmasIntoFields(SourceFile result)
    {
        // !!! pragma-to-field processing pending (see PORT-NOTE)
    }
}
