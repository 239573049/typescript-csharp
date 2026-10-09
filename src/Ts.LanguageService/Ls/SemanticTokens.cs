// Ported from tsgo internal/ls/semantictokens.go.
//
// PORT-NOTE: encodeSemanticTokens reads lsproto.GetClientCapabilities(ctx).TextDocument.
// SemanticTokens; the C# foundation has no per-request context carrier, so the resolved
// client capabilities are threaded as an explicit parameter on the entry points and
// encodeSemanticTokens (null = the client declared nothing, matching Go's
// slices.Contains-over-nil semantics: every token is filtered out). l.getScript +
// l.converters dissolve into the static LsDeps position codec. ctx.Err() checks map to
// CancellationToken.IsCancellationRequested. Positions are UTF-16 code units. Go's local
// isInfinityOrNaNString deliberately omits "-Infinity" (unlike ast.IsInfinityOrNaNString);
// that difference is preserved.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using SemanticTokenModifier = Ts.LsProto.SemanticTokenModifier;
using SemanticTokenType = Ts.LsProto.SemanticTokenType;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // semantictokens.go:19 — tokenTypes defines the order of token types for encoding.
    private static readonly SemanticTokenType[] tokenTypes =
    [
        SemanticTokenType.Namespace,
        SemanticTokenType.Class,
        SemanticTokenType.Enum,
        SemanticTokenType.Interface,
        SemanticTokenType.Struct,
        SemanticTokenType.TypeParameter,
        SemanticTokenType.Type,
        SemanticTokenType.Parameter,
        SemanticTokenType.Variable,
        SemanticTokenType.Property,
        SemanticTokenType.EnumMember,
        SemanticTokenType.Decorator,
        SemanticTokenType.Event,
        SemanticTokenType.Function,
        SemanticTokenType.Method,
        SemanticTokenType.Macro,
        SemanticTokenType.Label,
        SemanticTokenType.Comment,
        SemanticTokenType.String,
        SemanticTokenType.Keyword,
        SemanticTokenType.Number,
        SemanticTokenType.Regexp,
        SemanticTokenType.Operator,
    ];

    // semantictokens.go:46 — tokenModifiers defines the order of token modifiers for encoding.
    private static readonly SemanticTokenModifier[] tokenModifiers =
    [
        SemanticTokenModifier.Declaration,
        SemanticTokenModifier.Definition,
        SemanticTokenModifier.Readonly,
        SemanticTokenModifier.Static,
        SemanticTokenModifier.Deprecated,
        SemanticTokenModifier.Abstract,
        SemanticTokenModifier.Async,
        SemanticTokenModifier.Modification,
        SemanticTokenModifier.Documentation,
        SemanticTokenModifier.DefaultLibrary,
        new SemanticTokenModifier("local"),
    ];

    // semantictokens.go:60-86 — tokenType (encoding index enum).
    private enum TokenType
    {
        Namespace = 0,
        Class,
        Enum,
        Interface,
        Struct,
        TypeParameter,
        Type,
        Parameter,
        Variable,
        Property,
        EnumMember,
        Decorator,
        Event,
        Function,
        Method, // Previously called "member" in TypeScript
        Macro,
        Label,
        Comment,
        String,
        Keyword,
        Number,
        Regexp,
        Operator,
    }

    // semantictokens.go:88-102 — tokenModifier (bit flags).
    [Flags]
    private enum TokenModifier
    {
        // Go: tokenModifier(0).
        None = 0,
        Declaration = 1 << 0,
        Definition = 1 << 1,
        Readonly = 1 << 2,
        Static = 1 << 3,
        Deprecated = 1 << 4,
        Abstract = 1 << 5,
        Async = 1 << 6,
        Modification = 1 << 7,
        Documentation = 1 << 8,
        DefaultLibrary = 1 << 9,
        Local = 1 << 10,
    }

    // semantictokens.go:107 — SemanticTokensLegend returns the legend describing the token types and modifiers.
    // It filters the legend to only include types and modifiers that the client supports,
    // as indicated by clientCapabilities.
    public static global::Ts.LsProto.SemanticTokensLegend SemanticTokensLegend(global::Ts.LsProto.ResolvedSemanticTokensClientCapabilities clientCapabilities)
    {
        var types = new List<string>(tokenTypes.Length);
        foreach (var t in tokenTypes)
        {
            if (Array.IndexOf(clientCapabilities.TokenTypes ?? [], t.Value) >= 0)
            {
                types.Add(t.Value!);
            }
        }
        var modifiers = new List<string>(tokenModifiers.Length);
        foreach (var m in tokenModifiers)
        {
            if (Array.IndexOf(clientCapabilities.TokenModifiers ?? [], m.Value) >= 0)
            {
                modifiers.Add(m.Value!);
            }
        }
        return new global::Ts.LsProto.SemanticTokensLegend
        {
            TokenTypes = types.ToArray(),
            TokenModifiers = modifiers.ToArray(),
        };
    }

    // semantictokens.go:126 — ProvideSemanticTokens.
    public global::Ts.LsProto.SemanticTokens ProvideSemanticTokens(
        string documentURI,
        global::Ts.LsProto.ResolvedSemanticTokensClientCapabilities? clientCapabilities = null,
        CancellationToken cancellationToken = default)
    {
        var file = GetProgramAndFile(documentURI);
        var program = GetProgram();

        var (c, done) = host.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            var tokens = CollectSemanticTokens(cancellationToken, c, file, program);

            if (tokens.Count == 0)
            {
                return null!;
            }

            // Convert to LSP format (relative encoding)
            var encoded = EncodeSemanticTokens(cancellationToken, tokens, file, clientCapabilities);

            return new global::Ts.LsProto.SemanticTokens
            {
                Data = encoded,
            };
        }
        finally
        {
            done();
        }
    }

    // semantictokens.go:148 — ProvideSemanticTokensRange.
    public global::Ts.LsProto.SemanticTokens ProvideSemanticTokensRange(
        string documentURI,
        global::Ts.LsProto.Range rng,
        global::Ts.LsProto.ResolvedSemanticTokensClientCapabilities? clientCapabilities = null,
        CancellationToken cancellationToken = default)
    {
        var file = GetProgramAndFile(documentURI);
        var program = GetProgram();

        var (c, done) = host.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            var start = LsDeps.LineAndCharacterToPosition(file, rng.Start.Line, rng.Start.Character);
            var end = LsDeps.LineAndCharacterToPosition(file, rng.End.Line, rng.End.Character);

            var tokens = CollectSemanticTokensInRange(cancellationToken, c, file, program, start, end);

            if (tokens.Count == 0)
            {
                return null!;
            }

            // Convert to LSP format (relative encoding)
            var encoded = EncodeSemanticTokens(cancellationToken, tokens, file, clientCapabilities);

            return new global::Ts.LsProto.SemanticTokens
            {
                Data = encoded,
            };
        }
        finally
        {
            done();
        }
    }

    // semantictokens.go:173 — semanticToken.
    private readonly record struct SemanticToken(Node node, TokenType tokenType, TokenModifier tokenModifier);

    // semantictokens.go:179 — collectSemanticTokens.
    private List<SemanticToken> CollectSemanticTokens(CancellationToken cancellationToken, global::Ts.Checker.Checker c, SourceFile file, global::Ts.Compiler.Program program)
    {
        return CollectSemanticTokensInRange(cancellationToken, c, file, program, file.Pos, file.End);
    }

    // semantictokens.go:183 — collectSemanticTokensInRange.
    private List<SemanticToken> CollectSemanticTokensInRange(CancellationToken cancellationToken, global::Ts.Checker.Checker c, SourceFile file, global::Ts.Compiler.Program program, int spanStart, int spanEnd)
    {
        var tokens = new List<SemanticToken>();

        var inJSXElement = false;

        bool Visit(Node? node)
        {
            // Check for cancellation
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (node == null)
            {
                return false;
            }
            if ((node.Flags & NodeFlags.Reparsed) != 0)
            {
                return false;
            }
            var nodeEnd = node.End;
            if (node.Pos >= spanEnd || nodeEnd <= spanStart)
            {
                return false;
            }

            var prevInJSXElement = inJSXElement;
            if (NodeTests.IsJsxElement(node) || NodeTests.IsJsxSelfClosingElement(node))
            {
                inJSXElement = true;
            }
            else if (NodeTests.IsJsxExpression(node))
            {
                inJSXElement = false;
            }

            if (NodeTests.IsIdentifier(node) && node.Text() != "" && !inJSXElement && !IsInImportClause(node) && !IsInfinityOrNaNStringLocal(node.Text()))
            {
                var symbol = c.GetSymbolAtLocation(node);
                if (symbol != null)
                {
                    // Resolve aliases
                    if ((symbol.Flags & SymbolFlags.Alias) != 0)
                    {
                        symbol = c.GetAliasedSymbol(symbol)!;
                    }

                    if (ClassifySymbol(symbol, LsMeaning.GetMeaningFromLocation(node), out var tokenType))
                    {
                        var tokenModifier = TokenModifier.None;

                        // Check if this is a declaration
                        var parent = node.Parent;
                        if (parent != null)
                        {
                            var parentIsDeclaration = NodeTests.IsBindingElement(parent) || TokenFromDeclarationMapping(parent.Kind) == tokenType;
                            if (parentIsDeclaration && parent.Name() == node)
                            {
                                tokenModifier |= TokenModifier.Declaration;
                            }
                        }

                        // Property declaration in constructor: reclassify parameters as properties in property access context
                        if (tokenType == TokenType.Parameter && AstUtilities.IsRightSideOfQualifiedNameOrPropertyAccess(node))
                        {
                            tokenType = TokenType.Property;
                        }

                        // Type-based reclassification
                        tokenType = ReclassifyByType(c, node, tokenType);

                        // Get the value declaration to check modifiers
                        if (symbol.ValueDeclaration is { } decl)
                        {
                            var modifiers = AstUtilities.GetCombinedModifierFlags(decl);
                            var nodeFlags = AstUtilities.GetCombinedNodeFlags(decl);

                            if ((modifiers & ModifierFlags.Static) != 0)
                            {
                                tokenModifier |= TokenModifier.Static;
                            }
                            if ((modifiers & ModifierFlags.Async) != 0)
                            {
                                tokenModifier |= TokenModifier.Async;
                            }
                            if (tokenType != TokenType.Class && tokenType != TokenType.Interface)
                            {
                                if ((modifiers & ModifierFlags.Readonly) != 0 || (nodeFlags & NodeFlags.Const) != 0 || (symbol.Flags & SymbolFlags.EnumMember) != 0)
                                {
                                    tokenModifier |= TokenModifier.Readonly;
                                }
                            }
                            if ((tokenType == TokenType.Variable || tokenType == TokenType.Function) && IsLocalDeclaration(decl, file))
                            {
                                tokenModifier |= TokenModifier.Local;
                            }
                            var declSourceFile = AstUtilities.GetSourceFileOfNode(decl);
                            if (declSourceFile != null && program.IsSourceFileDefaultLibrary(declSourceFile.Path()))
                            {
                                tokenModifier |= TokenModifier.DefaultLibrary;
                            }
                        }
                        else if (symbol.Declarations != null)
                        {
                            foreach (var d in symbol.Declarations)
                            {
                                var declSourceFile = AstUtilities.GetSourceFileOfNode(d);
                                if (declSourceFile != null && program.IsSourceFileDefaultLibrary(declSourceFile.Path()))
                                {
                                    tokenModifier |= TokenModifier.DefaultLibrary;
                                    break;
                                }
                            }
                        }

                        tokens.Add(new SemanticToken(node, tokenType, tokenModifier));
                    }
                }
            }

            node.ForEachChild(Visit);
            inJSXElement = prevInJSXElement;
            return false;
        }

        Visit(file.AsNode());

        // Check for cancellation after collection
        if (cancellationToken.IsCancellationRequested)
        {
            return new List<SemanticToken>();
        }

        return tokens;
    }

    // semantictokens.go:299 — classifySymbol.
    private static bool ClassifySymbol(global::Ts.Ast.Symbol symbol, SemanticMeaning meaning, out TokenType tokenType)
    {
        var flags = symbol.Flags;
        if ((flags & SymbolFlags.Class) != 0)
        {
            tokenType = TokenType.Class;
            return true;
        }
        if ((flags & SymbolFlags.Enum) != 0)
        {
            tokenType = TokenType.Enum;
            return true;
        }
        if ((flags & SymbolFlags.TypeAlias) != 0)
        {
            tokenType = TokenType.Type;
            return true;
        }
        if ((flags & SymbolFlags.Interface) != 0)
        {
            if ((meaning & SemanticMeaning.Type) != 0)
            {
                tokenType = TokenType.Interface;
                return true;
            }
        }
        if ((flags & SymbolFlags.TypeParameter) != 0)
        {
            tokenType = TokenType.TypeParameter;
            return true;
        }

        // Check the value declaration
        var decl = symbol.ValueDeclaration;
        if (decl == null && symbol.Declarations is { Length: > 0 })
        {
            decl = symbol.Declarations[0];
        }
        if (decl != null)
        {
            if (NodeTests.IsBindingElement(decl))
            {
                decl = GetDeclarationForBindingElement(decl);
            }
            if (TokenFromDeclarationMapping(decl.Kind) is { } mapped)
            {
                tokenType = mapped;
                return true;
            }
        }

        tokenType = default;
        return false;
    }

    // semantictokens.go:336 — tokenFromDeclarationMapping. (Go returns -1 for "no mapping";
    // C# returns null.)
    private static TokenType? TokenFromDeclarationMapping(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.VariableDeclaration:
                return TokenType.Variable;
            case SyntaxKind.Parameter:
                return TokenType.Parameter;
            case SyntaxKind.PropertyDeclaration:
                return TokenType.Property;
            case SyntaxKind.ModuleDeclaration:
                return TokenType.Namespace;
            case SyntaxKind.EnumDeclaration:
                return TokenType.Enum;
            case SyntaxKind.EnumMember:
                return TokenType.EnumMember;
            case SyntaxKind.ClassDeclaration or SyntaxKind.ClassExpression:
                return TokenType.Class;
            case SyntaxKind.MethodDeclaration:
                return TokenType.Method;
            case SyntaxKind.FunctionDeclaration or SyntaxKind.FunctionExpression:
                return TokenType.Function;
            case SyntaxKind.MethodSignature:
                return TokenType.Method;
            case SyntaxKind.GetAccessor or SyntaxKind.SetAccessor:
                return TokenType.Property;
            case SyntaxKind.PropertySignature:
                return TokenType.Property;
            case SyntaxKind.InterfaceDeclaration:
                return TokenType.Interface;
            case SyntaxKind.TypeAliasDeclaration:
                return TokenType.Type;
            case SyntaxKind.TypeParameter:
                return TokenType.TypeParameter;
            case SyntaxKind.PropertyAssignment or SyntaxKind.ShorthandPropertyAssignment:
                return TokenType.Property;
            default:
                return null;
        }
    }

    // semantictokens.go:375 — reclassifyByType.
    private static TokenType ReclassifyByType(global::Ts.Checker.Checker c, Node node, TokenType tt)
    {
        // Type-based reclassification for variables, properties, and parameters
        if (tt == TokenType.Variable || tt == TokenType.Property || tt == TokenType.Parameter)
        {
            var typ = c.GetTypeAtLocation(node);
            if (typ != null)
            {
                bool Test(Func<global::Ts.Checker.Type, bool> condition)
                {
                    if (condition(typ))
                    {
                        return true;
                    }
                    if ((typ.Flags & global::Ts.Checker.TypeFlags.Union) != 0)
                    {
                        if (Array.FindIndex(typ.AsUnionType().Types(), t => condition(t)) >= 0)
                        {
                            return true;
                        }
                    }
                    return false;
                }

                // Check for constructor signatures (class-like)
                if (tt != TokenType.Parameter && Test(t => c.GetSignaturesOfType(t, global::Ts.Checker.SignatureKind.Construct).Length > 0))
                {
                    return TokenType.Class;
                }

                // Check for call signatures (function-like)
                // Must have call signatures AND (no properties OR be used in call context)
                var hasCallSignatures = Test(t => c.GetSignaturesOfType(t, global::Ts.Checker.SignatureKind.Call).Length > 0);
                if (hasCallSignatures)
                {
                    var hasNoProperties = !Test(t =>
                    {
                        var objType = t.AsObjectType();
                        return objType != null && objType.Properties().Length > 0;
                    });
                    if (hasNoProperties || IsExpressionInCallExpression(node))
                    {
                        if (tt == TokenType.Property)
                        {
                            return TokenType.Method;
                        }
                        return TokenType.Function;
                    }
                }
            }
        }
        return tt;
    }

    // semantictokens.go:421 — isLocalDeclaration.
    private static bool IsLocalDeclaration(Node decl, SourceFile sourceFile)
    {
        if (NodeTests.IsBindingElement(decl))
        {
            decl = GetDeclarationForBindingElement(decl);
        }
        if (NodeTests.IsVariableDeclaration(decl))
        {
            var parent = decl.Parent;
            // Check if this is a catch clause parameter
            if (parent != null && NodeTests.IsCatchClause(parent))
            {
                return AstUtilities.GetSourceFileOfNode(decl) == sourceFile;
            }
            if (parent != null && NodeTests.IsVariableDeclarationList(parent))
            {
                var grandparent = parent.Parent;
                if (grandparent != null)
                {
                    var greatGrandparent = grandparent.Parent;
                    return (!NodeTests.IsSourceFile(greatGrandparent!) || NodeTests.IsCatchClause(grandparent)) &&
                        AstUtilities.GetSourceFileOfNode(decl) == sourceFile;
                }
            }
        }
        else if (NodeTests.IsFunctionDeclaration(decl))
        {
            var parent = decl.Parent;
            return parent != null && !NodeTests.IsSourceFile(parent) && AstUtilities.GetSourceFileOfNode(decl) == sourceFile;
        }
        return false;
    }

    // semantictokens.go:446 — getDeclarationForBindingElement.
    private static Node GetDeclarationForBindingElement(Node element)
    {
        while (true)
        {
            var parent = element.Parent;
            if (parent != null && AstUtilities.IsBindingPattern(parent))
            {
                var grandparent = parent.Parent;
                if (grandparent != null && NodeTests.IsBindingElement(grandparent))
                {
                    element = grandparent;
                    continue;
                }
                return parent.Parent!;
            }
            return element;
        }
    }

    // semantictokens.go:461 — isInImportClause.
    private static bool IsInImportClause(Node node)
    {
        var parent = node.Parent;
        return parent != null && (NodeTests.IsImportClause(parent) || NodeTests.IsImportSpecifier(parent) || NodeTests.IsNamespaceImport(parent));
    }

    // semantictokens.go:466 — isExpressionInCallExpression.
    private static bool IsExpressionInCallExpression(Node node)
    {
        while (AstUtilities.IsRightSideOfQualifiedNameOrPropertyAccess(node))
        {
            node = node.Parent!;
        }
        var parent = node.Parent;
        return parent != null && NodeTests.IsCallExpression(parent) && parent.Expression() == node;
    }

    // semantictokens.go:474 — isInfinityOrNaNString. (Local variant: unlike
    // ast.IsInfinityOrNaNString it does not accept "-Infinity".)
    private static bool IsInfinityOrNaNStringLocal(string text)
    {
        return text == "Infinity" || text == "NaN";
    }

    // semantictokens.go:480 — encodeSemanticTokens. encodes tokens into the LSP format using relative positioning.
    // It filters tokens based on client capabilities, only including types and modifiers that the client supports.
    private static uint[] EncodeSemanticTokens(CancellationToken cancellationToken, List<SemanticToken> tokens, SourceFile file, global::Ts.LsProto.ResolvedSemanticTokensClientCapabilities? clientCapabilitiesOrNull)
    {
        // Go: lsproto.GetClientCapabilities(ctx).TextDocument.SemanticTokens — the
        // foundation threads the resolved capability explicitly; nil = declared nothing.
        var clientCapabilities = clientCapabilitiesOrNull ?? new global::Ts.LsProto.ResolvedSemanticTokensClientCapabilities { TokenTypes = [], TokenModifiers = [] };

        // Build mapping from server token types/modifiers to client indices
        var typeMapping = new Dictionary<TokenType, uint>();
        var modifierMapping = new Dictionary<SemanticTokenModifier, uint>();

        // Map server token types to client-supported indices
        uint clientIdx = 0;
        for (var i = 0; i < tokenTypes.Length; i++)
        {
            var serverType = tokenTypes[i];
            if (Array.IndexOf(clientCapabilities.TokenTypes ?? [], serverType.Value) >= 0)
            {
                typeMapping[(TokenType)i] = clientIdx;
                clientIdx++;
            }
        }

        // Map server token modifiers to client-supported bit positions
        uint clientBit = 0;
        foreach (var serverModifier in tokenModifiers)
        {
            if (Array.IndexOf(clientCapabilities.TokenModifiers ?? [], serverModifier.Value) >= 0)
            {
                modifierMapping[serverModifier] = clientBit;
                clientBit++;
            }
        }

        // Each token encodes 5 uint32 values: deltaLine, deltaChar, length, tokenType, tokenModifiers
        var encoded = new List<uint>(tokens.Count * 5);
        uint prevLine = 0;
        uint prevChar = 0;

        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip tokens with types not supported by the client
            if (!typeMapping.TryGetValue(token.tokenType, out var clientTypeIdx))
            {
                continue;
            }

            // Map modifiers to client-supported bit mask
            uint clientModifierMask = 0;
            for (var i = 0; i < tokenModifiers.Length; i++)
            {
                var serverModifier = tokenModifiers[i];
                if ((token.tokenModifier & (TokenModifier)(1 << i)) != 0)
                {
                    if (modifierMapping.TryGetValue(serverModifier, out var bit))
                    {
                        clientModifierMask |= 1u << (int)bit;
                    }
                }
            }

            // Use GetTokenPosOfNode to skip trivia (comments, whitespace) before the identifier
            var tokenStart = global::Ts.Scanner.Scanner.GetTokenPosOfNode(token.node, file, false);
            var tokenEnd = token.node.End;

            // Convert both start and end positions to LSP coordinates, then compute length
            var startPos = LsDeps.PositionToLineAndCharacter(file, tokenStart);
            var endPos = LsDeps.PositionToLineAndCharacter(file, tokenEnd);

            // Length is the character difference when on the same line
            uint tokenLength;
            if (startPos.Line == endPos.Line)
            {
                tokenLength = endPos.Character - startPos.Character;
            }
            else
            {
                // Go panics; InvalidOperationException is the repository's panic mapping.
                throw new InvalidOperationException($"semantic tokens: token spans multiple lines: start=({startPos.Line},{startPos.Character}) end=({endPos.Line},{endPos.Character}) for token at offset {tokenStart}");
            }

            var line = startPos.Line;
            var char_ = startPos.Character;

            // Verify that positions are strictly increasing (visitor walks in order)
            if (encoded.Count > 0 && (line < prevLine || (line == prevLine && char_ <= prevChar)))
            {
                throw new InvalidOperationException($"semantic tokens: positions must be strictly increasing: prev=({prevLine},{prevChar}) current=({line},{char_}) for token at offset {tokenStart}");
            }

            // Encode as: [deltaLine, deltaChar, length, tokenType, tokenModifiers]
            var deltaLine = line - prevLine;
            uint deltaChar;
            if (deltaLine == 0)
            {
                deltaChar = char_ - prevChar;
            }
            else
            {
                deltaChar = char_;
            }

            encoded.AddRange(
            [
                deltaLine,
                deltaChar,
                tokenLength,
                clientTypeIdx,
                clientModifierMask,
            ]);

            prevLine = line;
            prevChar = char_;
        }

        return encoded.ToArray();
    }
}
