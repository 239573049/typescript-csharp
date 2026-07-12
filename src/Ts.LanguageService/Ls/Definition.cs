// Ported from tsgo internal/ls/definition.go (go-to-definition foundation subset).

using System.Collections.Generic;
using System.Threading;

namespace Ts.Ls;

public sealed record DefinitionLocation(string FileName, global::Ts.Core.TextRange TextSpan);

public sealed partial class LanguageService
{
    public DefinitionLocation[] GetDefinitionAtPosition(
        string fileName,
        int position,
        CancellationToken cancellationToken = default)
    {
        var node = GetNodeAtPosition(fileName, position, out var file);
        if (node.Kind == global::Ts.Ast.SyntaxKind.SourceFile)
        {
            return [];
        }

        var (checker, done) = host.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            // The full Go worker calls GetSymbolAtLocation, resolves aliases, and
            // returns symbol.Declarations. That public checker surface has not landed
            // yet. GetReferencedValueDeclarations is the already-exported checker
            // query with the same common value-reference destination semantics. It
            // preserves the declaration-location pipeline without reaching into
            // internals, but the current checker's name resolver still returns no
            // symbol for an ordinary bound identifier use-site; the smoke is gated
            // until GetSymbolAtLocation becomes public.
            var declarations = checker.GetEmitResolver().GetReferencedValueDeclarations(node);
            return CreateDefinitionLocations(declarations);
        }
        finally
        {
            done();
        }
    }

    private static DefinitionLocation[] CreateDefinitionLocations(global::Ts.Ast.Node[] declarations)
    {
        var locations = new List<DefinitionLocation>(declarations.Length);
        var seen = new HashSet<FileRange>();

        foreach (var declaration in declarations)
        {
            // Go unconditionally dereferences the declaration's source file.
            var file = global::Ts.Ast.AstUtilities.GetSourceFileOfNode(declaration)!;
            var name = global::Ts.Ast.AstUtilities.GetNameOfDeclaration(declaration) ?? declaration;
            global::Ts.Core.TextRange textSpan;
            if (name.Kind == global::Ts.Ast.SyntaxKind.EmptyStatement)
            {
                textSpan = new global::Ts.Core.TextRange(name.Pos, name.Pos);
            }
            else
            {
                textSpan = CreateRangeFromNode(name, file);
            }

            var fileRange = new FileRange(file.FileName(), textSpan);
            if (seen.Add(fileRange))
            {
                locations.Add(new DefinitionLocation(fileRange.FileName, fileRange.TextSpan));
            }
        }

        return locations.ToArray();
    }

    // utilities.go:createRangeFromNode
    private static global::Ts.Core.TextRange CreateRangeFromNode(
        global::Ts.Ast.Node node,
        global::Ts.Ast.SourceFile file)
    {
        return new global::Ts.Core.TextRange(
            global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, file, false /*includeJSDoc*/),
            node.End);
    }

    private readonly record struct FileRange(string FileName, global::Ts.Core.TextRange TextSpan);
}
