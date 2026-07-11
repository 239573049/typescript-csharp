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
            // PORT-NOTE: resolveJSDoc (ast.go:2612) requires the parser hook parseJSDocForNode,
            // which is deferred until the parser lands (see SourceFile.cs SCOPE note). Lazy-JSDoc
            // source files are therefore not yet supported through this accessor; eager JSDoc
            // (populated in jsdocCache at parse time) is fully supported. Fails loudly rather than
            // silently diverging from Go behaviour.
            throw new NotImplementedException(
                "SourceFile lazy JSDoc resolution (resolveJSDoc) requires the not-yet-ported parser JSDoc hook");
        }
        // Go reads file.jsdocCache[node]; a nil map yields nil. jsdocCache may be null here.
        return file.jsdocCache?.GetValueOrDefault(node);
    }
}
