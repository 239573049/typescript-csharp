// Lazy JSDoc parsing hook for the AST layer (mirrors ast.go:13-37 + ast.go:2612 resolveJSDoc).
//
// For TS/TSX source files, the parser defers JSDoc parsing to first access (SourceFile.hasLazyJSDoc).
// Node.JSDoc(file) routes lazy files here; the parser package registers ParseForNode at module init
// (Parser.RegisterJSDocParseHook), equivalent to Go's parser init() calling ast.SetParseJSDocForNode.
//
// PORT-NOTE: Go uses a per-SourceFile sync.RWMutex (jsdocMu). To avoid modifying the existing
// SourceFile.cs (owned by a parallel cluster), this uses a lock on the SourceFile instance. The cache
// and parse-once semantics (double-checked lookup) match Go's resolveJSDoc.
using System;
using System.Collections.Generic;

namespace Ts.Ast;

public static class JSDocLazyResolve
{
    // Set by the parser package at module-init (ast.go:22 SetParseJSDocForNode).
    public static Func<SourceFile, Node, Node[]?>? ParseForNode;

    // ast.go:2612 — resolveJSDoc: parse-and-cache on first access, double-checked under a lock.
    internal static Node[]? ResolveJSDoc(this SourceFile file, Node n)
    {
        Func<SourceFile, Node, Node[]?> parse = ParseForNode
            ?? throw new InvalidOperationException("resolveJSDoc called but parseJSDocForNode is not registered; ensure the parser is loaded");
        // Fast path: check cache first.
        if (file.jsdocCache != null && file.jsdocCache.TryGetValue(n, out Node[]? cached))
        {
            return cached;
        }
        lock (file)
        {
            // Double-check after acquiring the lock.
            if (file.jsdocCache != null && file.jsdocCache.TryGetValue(n, out Node[]? existing))
            {
                return existing;
            }
            Node[]? jsdocs = parse(file, n);
            file.jsdocCache ??= new Dictionary<Node, Node[]>(ReferenceEqualityComparer.Instance);
            file.jsdocCache[n] = jsdocs!;
            return jsdocs;
        }
    }
}
