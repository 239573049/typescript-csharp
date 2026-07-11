using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Core;

namespace Ts.Ast;

// Go: internal/ast/ast.go — the SourceFile synthesized-token cache (TokenCacheKey :2459,
// the tokenCache/tokenFactory fields :2532-2534, GetOrCreateToken :2772 and createToken :2805).
// Listed as deferred in SourceFile.cs's SCOPE note ("GetOrCreateToken / createToken + token
// cache (ast.go:2774-2836)"); landed here as the phase-3 astnav port needs it (astnav
// constructs the synthesized tokens that are not stored in the AST).

// ast.go:2459
public readonly record struct TokenCacheKey(Node Parent, TextRange Loc);

public sealed partial class SourceFile
{
    private readonly Lock tokenCacheMu = new();
    private Dictionary<TokenCacheKey, Node>? tokenCache;
    private NodeFactory? tokenFactory;

    // ast.go:2651-2653 — the only hand-written node's VisitEachChild (generated nodes already
    // have theirs in Visitors.g.cs). Listed as deferred in SourceFile.cs's SCOPE note; landed
    // here because astnav navigates the tree by driving the visitor hooks through VisitEachChild.
    public override Node VisitEachChild(NodeVisitor v)
    {
        return v.Factory.UpdateSourceFile(this, v.visitTopLevelStatements(Statements), v.visitToken(EndOfFileToken));
    }

    // Gets a token from the file's token cache, or creates it if it does not already exist.
    // This function should NOT be used for creating synthetic tokens that are not in the file
    // in the first place. (ast.go:2772)
    public Node GetOrCreateToken(SyntaxKind kind, int pos, int end, Node parent, TokenFlags flags)
    {
        lock (tokenCacheMu)
        {
            var loc = new TextRange(pos, end);
            var key = new TokenCacheKey(parent, loc);
            if (tokenCache != null && tokenCache.TryGetValue(key, out var token))
            {
                if (token.Kind != kind)
                {
                    throw new InvalidOperationException($"Token cache mismatch: {token.Kind} != {kind}");
                }
                return token;
            }
            if ((parent.Flags & NodeFlags.Reparsed) != 0)
            {
                throw new InvalidOperationException($"Cannot create token from reparsed node of kind {parent.Kind}");
            }
            tokenCache ??= new Dictionary<TokenCacheKey, Node>();
            var created = CreateToken(kind, pos, end, flags);
            created.Loc = loc;
            created.Parent = parent;
            tokenCache[key] = created;
            return created;
        }
    }

    // `kind` should be a token kind. (ast.go:2805 — Go's package-level createToken with the
    // SourceFile as its `file` receiver; here an instance method on that receiver.)
    private Node CreateToken(SyntaxKind kind, int pos, int end, TokenFlags flags)
    {
        tokenFactory ??= new NodeFactory(new NodeFactoryHooks());
        var tokenText = text.Substring(pos, end - pos);
        switch (kind)
        {
            case SyntaxKind.NumericLiteral:
                return tokenFactory.NewNumericLiteral(tokenText, flags);
            case SyntaxKind.BigIntLiteral:
                return tokenFactory.NewBigIntLiteral(tokenText, flags);
            case SyntaxKind.StringLiteral:
                return tokenFactory.NewStringLiteral(tokenText, flags);
            case SyntaxKind.JsxText:
            case SyntaxKind.JsxTextAllWhiteSpaces:
                return tokenFactory.NewJsxText(tokenText, kind == SyntaxKind.JsxTextAllWhiteSpaces);
            case SyntaxKind.RegularExpressionLiteral:
                return tokenFactory.NewRegularExpressionLiteral(tokenText, flags);
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                return tokenFactory.NewNoSubstitutionTemplateLiteral(tokenText, flags);
            case SyntaxKind.TemplateHead:
                return tokenFactory.NewTemplateHead(tokenText, "" /*rawText*/, flags);
            case SyntaxKind.TemplateMiddle:
                return tokenFactory.NewTemplateMiddle(tokenText, "" /*rawText*/, flags);
            case SyntaxKind.TemplateTail:
                return tokenFactory.NewTemplateTail(tokenText, "" /*rawText*/, flags);
            case SyntaxKind.Identifier:
                return tokenFactory.NewIdentifier(tokenText);
            case SyntaxKind.PrivateIdentifier:
                return tokenFactory.NewPrivateIdentifier(tokenText);
            default: // Punctuation and keywords
                return tokenFactory.NewToken(kind);
        }
    }
}

public sealed partial class NodeFactory
{
    // ast.go:2691-2698 — UpdateSourceFile. Referenced by SourceFile.VisitEachChild; listed as
    // deferred in SourceFile.cs's SCOPE note, landed here with it. For astnav's navigation hooks
    // (which return nodes unchanged) this always takes the fast path returning the original node.
    public Node UpdateSourceFile(SourceFile node, StatementList? statements, TokenNode? endOfFileToken)
    {
        if (!ReferenceEquals(statements, node.Statements) || !ReferenceEquals(endOfFileToken, node.EndOfFileToken))
        {
            var updated = NewSourceFile(node.parseOptions, node.text, statements, endOfFileToken).AsSourceFile();
            updated.CopyFrom(node);
            return UpdateNode(updated.AsNode(), node.AsNode(), hooks);
        }
        return node.AsNode();
    }
}
