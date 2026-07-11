using System;
using System.Collections.Generic;

namespace Ts.Ast;

// Go: internal/ast/ast.go:1560 — (*Node).JSDoc. Node.cs's header lists JSDoc/EagerJSDoc as
// deferred to "the phase-3 ast package port"; landed here (as an extension method, so the Node
// class definition is untouched) because astnav is the first consumer. Call form node.JSDoc(file)
// is preserved.
public static class NodeJSDocAccessors
{
    // ast.go:1560
    public static Node[]? JSDoc(this Node node, SourceFile? file)
    {
        if ((node.Flags & NodeFlags.HasJSDoc) == 0)
        {
            return null;
        }
        if (file == null)
        {
            file = AstUtilities.GetSourceFileOfNode(node);
            if (file == null)
            {
                return null;
            }
        }
        if (file.hasLazyJSDoc)
        {
            // resolveJSDoc (ast.go:2612): parse JSDoc lazily on first access. The parser package
            // registers the hook (JSDocLazyResolve.ParseForNode) at module init.
            return file.ResolveJSDoc(node);
        }
        // Go reads file.jsdocCache[node]; a nil map yields nil. jsdocCache may be null here.
        return file.jsdocCache?.GetValueOrDefault(node);
    }
}
