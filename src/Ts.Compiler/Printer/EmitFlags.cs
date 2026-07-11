// Ported from tsgo internal/printer/emitflags.go

using System;

namespace Ts.Printer;

[Flags]
public enum EmitFlags : uint
{
    None = 0,

    SingleLine = 1 << 0, // The contents of this node should be emitted on a single line.
    MultiLine = 1 << 1, // The contents of this node should be emitted on multiple lines.
    NoLeadingSourceMap = 1 << 2, // Do not emit a leading source map location for this node.
    NoTrailingSourceMap = 1 << 3, // Do not emit a trailing source map location for this node.
    NoNestedSourceMaps = 1 << 4, // Do not emit source map locations for children of this node.
    NoTokenLeadingSourceMaps = 1 << 5, // Do not emit leading source map location for token nodes.
    NoTokenTrailingSourceMaps = 1 << 6, // Do not emit trailing source map location for token nodes.
    NoLeadingComments = 1 << 7, // Do not emit leading comments for this node.
    NoTrailingComments = 1 << 8, // Do not emit trailing comments for this node.
    NoNestedComments = 1 << 9, // Do not emit nested comments for children of this node.
    HelperName = 1 << 10, // The Identifier refers to an *unscoped* emit helper (one that is emitted at the top of the file)
    ExportName = 1 << 11, // Ensure an export prefix is added for an identifier that points to an exported declaration with a local name (see SymbolFlags.ExportHasLocal).
    LocalName = 1 << 12, // Ensure an export prefix is not added for an identifier that points to an exported declaration.
    Indented = 1 << 13, // Adds an explicit extra indentation level for class and function bodies when printing (used to match old emitter).
    NoIndentation = 1 << 14, // Do not indent the node.
    ReuseTempVariableScope = 1 << 15, // Reuse the existing temp variable scope during emit.
    CustomPrologue = 1 << 16, // Treat the statement as if it were a prologue directive (NOTE: Prologue directives are *not* transformed).
    NoAsciiEscaping = 1 << 17, // When synthesizing nodes that lack an original node or textSourceNode, we want to write the text on the node with ASCII escaping substitutions.
    ExternalHelpers = 1 << 18, // This source file has external helpers
    StartOnNewLine = 1 << 19, // Start this node on a new line
    IndirectCall = 1 << 20, // Emit CallExpression as an indirect call: `(0, f)()`
    AsyncFunctionBody = 1 << 21, // The node was originally an async function body.
    NoLexicalArguments = 1 << 22, // Do not capture `arguments` for this arrow function. Set on arrows lowered from class static blocks, where `arguments` is an error; preserves Strada's emit behavior.
    TransformPrivateStaticElements = 1 << 23, // Indicates static private elements in a file or class should be transformed regardless of --target (used by esDecorators transform).
    NoLexicalThis = 1 << 24, // Do not capture `this` for this node's subtree. Set on relocated static initializers, where `this` is handled by the class fields transform.

    NoSourceMap = NoLeadingSourceMap | NoTrailingSourceMap, // Do not emit a source map location for this node.
    NoTokenSourceMaps = NoTokenLeadingSourceMaps | NoTokenTrailingSourceMaps, // Do not emit source map locations for tokens of this node.
    NoComments = NoLeadingComments | NoTrailingComments, // Do not emit comments for this node.
}
