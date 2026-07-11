// Ported from tsgo internal/parser/parser.go (recursive-descent core).
// Positions are UTF-16 code units (plan/01-architecture.md §6). The deep-recursion
// entry ParseSourceFile runs on the 64MB LargeStackExecutor thread (plan §4.6, R01).
using System;
using System.Collections.Generic;

using Ts.Ast;
using Ts.Collections;
using Ts.Core;

using Message = Ts.Diagnostics.Message;
using ScannerState = global::Ts.Scanner.ScannerState;

namespace Ts.Parser;

public enum ParsingContext
{
    SourceElements,           // Elements in source file
    BlockStatements,          // Statements in block
    SwitchClauses,            // Clauses in switch statement
    SwitchClauseStatements,   // Statements in switch clause
    TypeMembers,              // Members in interface or type literal
    ClassMembers,             // Members in class declaration
    EnumMembers,              // Members in enum declaration
    HeritageClauseElement,    // Elements in a heritage clause
    VariableDeclarations,     // Variable declarations in variable statement
    ObjectBindingElements,    // Binding elements in object binding list
    ArrayBindingElements,     // Binding elements in array binding list
    ArgumentExpressions,      // Expressions in argument list
    ObjectLiteralMembers,     // Members in object literal
    JsxAttributes,            // Attributes in jsx element
    JsxChildren,              // Things between opening and closing JSX tags
    ArrayLiteralMembers,      // Members in array literal
    Parameters,               // Parameters in parameter list
    JSDocParameters,          // JSDoc parameters in parameter list of JSDoc function type
    RestProperties,           // Property names in a rest type list
    TypeParameters,           // Type parameters in type parameter list
    TypeArguments,            // Type arguments in type argument list
    TupleElementTypes,        // Element types in tuple element type list
    HeritageClauses,          // Heritage clauses for a class or interface declaration.
    ImportOrExportSpecifiers, // Named import clause's import specifier list
    ImportAttributes,         // Import attributes
    JSDocComment,             // Parsing via JSDocParser
    Count,                    // Number of parsing contexts
}

// jsdocScannerInfo: cheap flags read off the scanner about a preceding JSDoc comment.
[Flags]
internal enum JSDocScannerInfo : byte
{
    None = 0,
    HasJSDoc = 1 << 0,
    HasDeprecated = 1 << 1,
    HasSeeOrLink = 1 << 2,
}

internal struct JSDocInfo
{
    public Node Parent;
    public Node[] JsDocs;
}

// Snapshot of speculative parser state; captured by Mark, restored by Rewind.
internal struct ParserState
{
    public ScannerState ScannerState;
    public NodeFlags ContextFlags;
    public int DiagnosticsLen;
    public int JsDiagnosticsLen;
    public int JsdocInfosLen;
    public int ReparsedClonesLen;
    public bool StatementHasAwaitIdentifier;
    public bool HasParseError;
}

public sealed partial class Parser
{
    // PORT-NOTE (plan §1.5): the `Scanner` using-alias works for static calls (expression
    // position) but the sibling namespace Ts.Scanner shadows it in type position, so the
    // field/ctor are written fully-qualified.
    private global::Ts.Scanner.Scanner scanner = null!;
    private NodeFactory factory = null!;

    private SourceFileParseOptions opts;
    private string sourceText = "";

    private ScriptKind scriptKind;
    private LanguageVariant languageVariant;
    private readonly List<Diagnostic> diagnostics = new();
    private readonly List<Diagnostic> jsDiagnostics = new();
    private readonly List<Diagnostic> jsdocDiagnostics = new();

    private SyntaxKind token;
    private NodeFlags sourceFlags;
    private NodeFlags contextFlags;
    private int parsingContexts;
    private bool statementHasAwaitIdentifier;
    private bool hasDeprecatedTag;
    private bool hasParseError;

    private Dictionary<string, string>? identifiers;
    private int identifierCount;
    private readonly Set<int> notParenthesizedArrow = new();
    private readonly List<JSDocInfo> jsdocInfos = new();
    private readonly List<int> possibleAwaitSpans = new();
    // jsdoc scratch spaces (mirrors Go's *Space slices; reused across withJSDoc calls)
    private List<CommentRange> jsdocCommentRangesSpace = new();
    private List<Node> reparseList = new();
    private Node? commonJSModuleIndicator;

    private Node? currentParent;
    private Visitor setParentFromContext = null!;
    private readonly List<Node> reparsedClones = new();

    private Parser()
    {
        InitializeClosures();
    }

    internal static Parser NewParser() => new();

    // GetViableKeywordSuggestions cache (Go: package-level var).
    private static readonly string[] viableKeywordSuggestions = global::Ts.Scanner.Scanner.GetViableKeywordSuggestions();

    // missingListNodes sentinel: distinguishes a "missing" list (opening token absent) from an empty list.
    private static readonly Node[] missingListNodes = new Node[0];

    private static bool IsMissingNodeList(NodeList? list) =>
        list != null && ReferenceEquals(list.Nodes, missingListNodes);

    private static readonly ObjectPool<Parser> parserPool =
        new(NewParser, static p => p.ClearForReturn());

    private static Parser GetParser() => parserPool.Get();

    private static void PutParser(Parser p) => parserPool.Return(p);

    // Mirrors Go putParser: `*p = Parser{scanner, setParentFromContext}` — reset all
    // accumulated state but keep the (reusable) scanner and the setParentFromContext closure.
    private void ClearForReturn()
    {
        opts = default;
        sourceText = "";
        scriptKind = default;
        languageVariant = default;
        diagnostics.Clear();
        jsDiagnostics.Clear();
        jsdocDiagnostics.Clear();
        token = default;
        sourceFlags = NodeFlags.None;
        contextFlags = NodeFlags.None;
        parsingContexts = 0;
        statementHasAwaitIdentifier = false;
        hasDeprecatedTag = false;
        hasParseError = false;
        identifiers = null;
        identifierCount = 0;
        notParenthesizedArrow.Clear();
        jsdocInfos.Clear();
        possibleAwaitSpans.Clear();
        jsdocCommentRangesSpace.Clear();
        reparseList = new List<Node>();
        commonJSModuleIndicator = null;
        currentParent = null;
        reparsedClones.Clear();
        factory = null!;
    }

    // Go: newParser().initializeClosures()
    private void InitializeClosures()
    {
        setParentFromContext = n =>
        {
            n.Parent = currentParent;
            return false;
        };
    }

    private bool IsJavaScript() => scriptKind == ScriptKind.JS || scriptKind == ScriptKind.Jsx;

    // ParseSourceFile is the public entry. Runs on the 64MB large-stack thread (R01/plan §4.6 E1).
    public static SourceFile ParseSourceFile(SourceFileParseOptions opts, string sourceText, ScriptKind scriptKind)
    {
        return LargeStackExecutor.Run(() =>
        {
            Parser p = GetParser();
            try
            {
                p.InitializeState(opts, sourceText, scriptKind);
                p.NextToken();
                if (p.scriptKind == ScriptKind.Json)
                {
                    return p.ParseJSONText();
                }
                return p.ParseSourceFileWorker();
            }
            finally
            {
                PutParser(p);
            }
        });
    }

    public static EntityName? ParseIsolatedEntityName(string text)
    {
        Parser p = GetParser();
        try
        {
            p.InitializeState(default, text, ScriptKind.JS);
            p.NextToken();
            EntityName entityName = p.ParseEntityName(true, null);
            return CoreUtil.IfElse<EntityName?>(p.token == SyntaxKind.EndOfFile && p.diagnostics.Count == 0, entityName, null);
        }
        finally
        {
            PutParser(p);
        }
    }

    private void InitializeState(SourceFileParseOptions opts, string sourceText, ScriptKind scriptKind)
    {
        if (scriptKind == ScriptKind.Unknown)
        {
            throw new InvalidOperationException("ScriptKind must be specified when parsing source file: " + opts.FileName);
        }

        if (scanner == null)
        {
            scanner = new global::Ts.Scanner.Scanner();
        }
        else
        {
            scanner.Reset();
        }
        // Each parse gets its own NodeFactory (independent node counts; plan task note).
        factory = new NodeFactory();
        this.opts = opts;
        this.sourceText = sourceText;
        this.scriptKind = scriptKind;
        languageVariant = Utilities.GetLanguageVariant(scriptKind);
        switch (scriptKind)
        {
            case ScriptKind.JS:
            case ScriptKind.Jsx:
                contextFlags = NodeFlags.JavaScriptFile;
                break;
            case ScriptKind.Json:
                contextFlags = NodeFlags.JavaScriptFile | NodeFlags.JsonFile;
                break;
            default:
                contextFlags = NodeFlags.None;
                break;
        }
        scanner.SetText(sourceText);
        scanner.SetOnError(ScanError);
        scanner.SetLanguageVariant(languageVariant);
    }

    private void ScanError(Message message, int pos, int length, params object?[] args)
    {
        ParseErrorAtRange(new TextRange(pos, pos + length), message, args);
    }

    private Diagnostic? ParseErrorAt(int pos, int end, Message message, params object?[] args)
    {
        return ParseErrorAtRange(new TextRange(pos, end), message, args);
    }

    private Diagnostic? ParseErrorAtCurrentToken(Message message, params object?[] args)
    {
        return ParseErrorAtRange(scanner.TokenRange(), message, args);
    }

    private Diagnostic? ParseErrorAtRange(TextRange loc, Message message, params object?[] args)
    {
        // Don't report another error if it would just be at the same location as the last error
        Diagnostic? result = null;
        if (diagnostics.Count == 0 || diagnostics[^1].Pos() != loc.Pos)
        {
            result = Diagnostic.NewDiagnostic(null, loc, message, args);
            diagnostics.Add(result);
        }
        hasParseError = true;
        return result;
    }

    // ── Speculation: Mark / Rewind ──

    private ParserState Mark()
    {
        return new ParserState
        {
            ScannerState = scanner.Mark(),
            ContextFlags = contextFlags,
            DiagnosticsLen = diagnostics.Count,
            JsDiagnosticsLen = jsDiagnostics.Count,
            JsdocInfosLen = jsdocInfos.Count,
            ReparsedClonesLen = reparsedClones.Count,
            StatementHasAwaitIdentifier = statementHasAwaitIdentifier,
            HasParseError = hasParseError,
        };
    }

    private void Rewind(ParserState state)
    {
        scanner.Rewind(state.ScannerState);
        token = scanner.Token();
        contextFlags = state.ContextFlags;
        TruncateTo(diagnostics, state.DiagnosticsLen);
        TruncateTo(jsDiagnostics, state.JsDiagnosticsLen);
        TruncateTo(jsdocInfos, state.JsdocInfosLen);
        TruncateTo(reparsedClones, state.ReparsedClonesLen);
        statementHasAwaitIdentifier = state.StatementHasAwaitIdentifier;
        hasParseError = state.HasParseError;
    }

    private static void TruncateTo<T>(List<T> list, int len)
    {
        if (list.Count > len)
        {
            list.RemoveRange(len, list.Count - len);
        }
    }

    private bool LookAhead(Func<bool> callback)
    {
        ParserState state = Mark();
        bool result = callback();
        Rewind(state);
        return result;
    }

    // Go: tryParse — like lookAhead but keeps state when the callback "succeeds" (returns truthy).
    private bool TryParse(Func<bool> callback)
    {
        ParserState state = Mark();
        bool result = callback();
        if (!result)
        {
            Rewind(state);
        }
        return result;
    }

    private T? TryParseNode<T>(Func<T?> callback) where T : class
    {
        ParserState state = Mark();
        T? result = callback();
        if (result == null)
        {
            Rewind(state);
        }
        return result;
    }

    private SyntaxKind NextToken()
    {
        // if the keyword had an escape, issue a parse error for the escape
        if (NodeTests.IsKeywordKind(token) && (scanner.HasUnicodeEscape() || scanner.HasExtendedUnicodeEscape()))
        {
            ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Keywords_cannot_contain_escape_characters);
        }
        token = scanner.Scan();
        return token;
    }

    private SyntaxKind NextTokenWithoutCheck()
    {
        token = scanner.Scan();
        return token;
    }

    private SyntaxKind NextTokenJSDoc()
    {
        token = scanner.ScanJSDocToken();
        return token;
    }

    private SyntaxKind NextJSDocCommentTextToken(bool inBackticks)
    {
        token = scanner.ScanJSDocCommentTextToken(inBackticks);
        return token;
    }

    private int NodePos() => scanner.TokenFullStart();

    private bool HasPrecedingLineBreak() => scanner.HasPrecedingLineBreak();

    private JSDocScannerInfo JsdocScannerInfo()
    {
        if (!scanner.HasPrecedingJSDocComment())
        {
            return JSDocScannerInfo.None;
        }
        JSDocScannerInfo info = JSDocScannerInfo.HasJSDoc;
        if (scanner.HasPrecedingJSDocWithDeprecatedTag())
        {
            info |= JSDocScannerInfo.HasDeprecated;
        }
        if (scanner.HasPrecedingJSDocWithSeeOrLink())
        {
            info |= JSDocScannerInfo.HasSeeOrLink;
        }
        return info;
    }

    // ── SourceFile top level ──

    private SourceFile ParseSourceFileWorker()
    {
        bool isDeclarationFile = global::Ts.TsPath.TsPath.IsDeclarationFileName(opts.FileName);
        if (isDeclarationFile)
        {
            contextFlags |= NodeFlags.Ambient;
        }
        int pos = NodePos();
        Node[] statements = ParseListIndex(ParsingContext.SourceElements, ParseToplevelStatement);
        int end = NodePos();
        JSDocScannerInfo endJSDoc = JsdocScannerInfo();
        Node eof = ParseTokenNode();
        WithJSDoc(eof, endJSDoc);
        if (eof.Kind != SyntaxKind.EndOfFile)
        {
            throw new InvalidOperationException("Expected end of file token from scanner.");
        }
        if (reparseList.Count != 0)
        {
            var combined = new List<Node>(statements);
            combined.AddRange(reparseList);
            statements = combined.ToArray();
            reparseList = new List<Node>();
        }
        Node node = FinishNode(factory.NewSourceFile(opts, sourceText, NewNodeList(new TextRange(pos, end), statements), eof), pos);
        SourceFile result = node.AsSourceFile();
        FinishSourceFile(result, isDeclarationFile);
        if (!result.IsDeclarationFile && result.ExternalModuleIndicator != null && possibleAwaitSpans.Count > 0)
        {
            Node reparse = FinishNode(ReparseTopLevelAwait(result), pos);
            if (node != reparse)
            {
                result = reparse.AsSourceFile();
                FinishSourceFile(result, isDeclarationFile);
            }
        }
        References.CollectExternalModuleReferences(result);
        if (AstUtilities.IsInJSFile(node))
        {
            result.SetJSDiagnostics(AttachFileToDiagnostics(jsDiagnostics.ToArray(), result));
        }
        return result;
    }

    private void FinishSourceFile(SourceFile result, bool isDeclarationFile)
    {
        result.CommentDirectives = scanner.CommentDirectives()?.ToArray();
        result.Pragmas = GetCommentPragmas(factory, sourceText);
        ProcessPragmasIntoFields(result);
        result.SetDiagnostics(AttachFileToDiagnostics(diagnostics.ToArray(), result));
        result.SetJSDocDiagnostics(AttachFileToDiagnostics(jsdocDiagnostics.ToArray(), result));
        result.CommonJSModuleIndicator = commonJSModuleIndicator;
        result.IsDeclarationFile = isDeclarationFile;
        result.LanguageVariant = languageVariant;
        result.ScriptKind = scriptKind;
        result.Flags |= sourceFlags;
        result.Identifiers = identifiers;
        result.NodeCount = factory.NodeCount();
        result.TextCount = factory.TextCount();
        result.IdentifierCount = identifierCount;
        result.SetJSDocCache(CreateJSDocCache());
        // For non-JS files, enable lazy JSDoc parsing on demand
        if (!IsJavaScript())
        {
            result.SetHasLazyJSDoc(true);
        }
        Node[] clones = reparsedClones.ToArray();
        Array.Sort(clones, References.CompareNodePositions);
        result.ReparsedClones = clones;
        References.SetExternalModuleIndicator(result, opts.ExternalModuleIndicatorOptions);
    }

    private Dictionary<Node, Node[]>? CreateJSDocCache()
    {
        if (jsdocInfos.Count == 0)
        {
            return null;
        }
        var result = new Dictionary<Node, Node[]>(jsdocInfos.Count, ReferenceEqualityComparer.Instance);
        foreach (JSDocInfo info in jsdocInfos) // map-iter: 序无关 (building a keyed cache)
        {
            result[info.Parent] = info.JsDocs;
        }
        return result;
    }

    private Node ParseToplevelStatement(int i)
    {
        statementHasAwaitIdentifier = false;
        Node statement = ParseStatement();
        // Reparsed nodes produced while parsing this statement are inserted before it.
        i += reparseList.Count;
        if (statementHasAwaitIdentifier && (statement.Flags & NodeFlags.AwaitContext) == 0)
        {
            if (possibleAwaitSpans.Count == 0 || possibleAwaitSpans[^1] != i)
            {
                possibleAwaitSpans.Add(i);
                possibleAwaitSpans.Add(i + 1);
            }
            else
            {
                possibleAwaitSpans[^1] = i + 1;
            }
        }
        return statement;
    }

    // ── list parsing ──

    private Node[] ParseListIndex(ParsingContext kind, Func<int, Node> parseElement)
    {
        int saveParsingContexts = parsingContexts;
        parsingContexts |= 1 << (int)kind;
        List<Node> outerReparseList = reparseList;
        reparseList = new List<Node>();
        var list = new List<Node>();
        for (int i = 0; !IsListTerminator(kind); i++)
        {
            if (IsListElement(kind, false))
            {
                Node elt = parseElement(list.Count);
                if (reparseList.Count != 0)
                {
                    foreach (Node e in reparseList)
                    {
                        // Propagate @typedef type alias declarations outwards to a context that permits them.
                        if ((NodeTests.IsJSTypeAliasDeclaration(e) || NodeTests.IsJSImportDeclaration(e)) &&
                            kind != ParsingContext.SourceElements && kind != ParsingContext.BlockStatements)
                        {
                            outerReparseList.Add(e);
                        }
                        else
                        {
                            list.Add(e);
                        }
                    }
                    reparseList = new List<Node>();
                }
                list.Add(elt);
                continue;
            }
            if (AbortParsingListOrMoveToNextToken(kind))
            {
                break;
            }
        }
        reparseList = outerReparseList;
        parsingContexts = saveParsingContexts;
        return list.ToArray();
    }

    private NodeList ParseList(ParsingContext kind, Func<Node> parseElement)
    {
        int pos = NodePos();
        Node[] nodes = ParseListIndex(kind, _ => parseElement());
        return NewNodeList(new TextRange(pos, NodePos()), nodes);
    }

    // Returns a non-nil (possibly empty) NodeList if parsing was successful, or nil if parseElement returned nil.
    private NodeList? ParseDelimitedList(ParsingContext kind, Func<Node?> parseElement)
    {
        int pos = NodePos();
        int saveParsingContexts = parsingContexts;
        parsingContexts |= 1 << (int)kind;
        var list = new List<Node>();
        while (true)
        {
            if (IsListElement(kind, false))
            {
                int startPos = NodePos();
                Node? element = parseElement();
                if (element == null)
                {
                    parsingContexts = saveParsingContexts;
                    return null;
                }
                list.Add(element);
                if (ParseOptional(SyntaxKind.CommaToken))
                {
                    continue;
                }
                if (IsListTerminator(kind))
                {
                    break;
                }
                // We didn't get a comma, and the list wasn't terminated; parse a comma for a good error.
                if (token != SyntaxKind.CommaToken && kind == ParsingContext.EnumMembers)
                {
                    ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.An_enum_member_name_must_be_followed_by_a_or);
                }
                else
                {
                    ParseExpected(SyntaxKind.CommaToken);
                }
                // If the token was a semicolon, and the caller allows that, skip it and continue.
                if ((kind == ParsingContext.ObjectLiteralMembers || kind == ParsingContext.ImportAttributes) &&
                    token == SyntaxKind.SemicolonToken && !HasPrecedingLineBreak())
                {
                    NextToken();
                }
                if (startPos == NodePos())
                {
                    // Consumed no tokens; consume one to avoid an infinite loop.
                    NextToken();
                }
                continue;
            }
            if (IsListTerminator(kind))
            {
                break;
            }
            if (AbortParsingListOrMoveToNextToken(kind))
            {
                break;
            }
        }
        parsingContexts = saveParsingContexts;
        return NewNodeList(new TextRange(pos, NodePos()), list.ToArray());
    }

    private NodeList? ParseBracketedList(ParsingContext kind, Func<Node?> parseElement, SyntaxKind opening, SyntaxKind closing)
    {
        if (ParseExpected(opening))
        {
            NodeList? result = ParseDelimitedList(kind, parseElement);
            ParseExpected(closing);
            return result;
        }
        return CreateMissingList();
    }

    private NodeList ParseEmptyNodeList() => NewNodeList(new TextRange(NodePos(), NodePos()), Array.Empty<Node>());

    private NodeList CreateMissingList()
    {
        NodeList result = ParseEmptyNodeList();
        result.Nodes = missingListNodes;
        return result;
    }

    // Returns true if we should abort parsing.
    private bool AbortParsingListOrMoveToNextToken(ParsingContext kind)
    {
        ParsingContextErrors(kind);
        if (IsInSomeParsingContext())
        {
            return true;
        }
        NextToken();
        return false;
    }

    private bool IsInSomeParsingContext()
    {
        Debug.Assert(parsingContexts != 0, "Missing parsing context");
        for (int kind = 0; kind < (int)ParsingContext.Count; kind++)
        {
            if ((parsingContexts & (1 << kind)) != 0)
            {
                if (IsListElement((ParsingContext)kind, true) || IsListTerminator((ParsingContext)kind))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // ── parseExpected / parseOptional family ──

    private bool ParseExpectedJSDoc(SyntaxKind kind)
    {
        if (token == kind)
        {
            NextTokenJSDoc();
            return true;
        }
        if (!Utilities.IsKeywordOrPunctuation(kind))
        {
            throw new InvalidOperationException("Invalid JSDoc kind: expected keyword or punctuation");
        }
        ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_0_expected, global::Ts.Scanner.Scanner.TokenToString(kind));
        return false;
    }

    private void ParseExpectedMatchingBrackets(SyntaxKind openKind, SyntaxKind closeKind, bool openParsed, int openPosition)
    {
        if (token == closeKind)
        {
            NextToken();
            return;
        }
        Diagnostic? lastError = ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_0_expected, global::Ts.Scanner.Scanner.TokenToString(closeKind));
        if (!openParsed)
        {
            return;
        }
        if (lastError != null)
        {
            Diagnostic related = Diagnostic.NewDiagnostic(null, new TextRange(openPosition, openPosition),
                global::Ts.Diagnostics.Diagnostics.The_parser_expected_to_find_a_1_to_match_the_0_token_here,
                global::Ts.Scanner.Scanner.TokenToString(openKind), global::Ts.Scanner.Scanner.TokenToString(closeKind));
            lastError.AddRelatedInfo(related);
        }
    }

    private bool ParseOptional(SyntaxKind kind)
    {
        if (token == kind)
        {
            NextToken();
            return true;
        }
        return false;
    }

    private bool ParseExpected(SyntaxKind kind) => ParseExpectedWithDiagnostic(kind, null, true);

    private bool ParseExpectedWithoutAdvancing(SyntaxKind kind) => ParseExpectedWithDiagnostic(kind, null, false);

    private bool ParseExpectedWithDiagnostic(SyntaxKind kind, Message? message, bool shouldAdvance)
    {
        if (token == kind)
        {
            if (shouldAdvance)
            {
                NextToken();
            }
            return true;
        }
        if (message != null)
        {
            ParseErrorAtCurrentToken(message);
        }
        else
        {
            ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_0_expected, global::Ts.Scanner.Scanner.TokenToString(kind));
        }
        return false;
    }

    private Node ParseTokenNode()
    {
        int pos = NodePos();
        SyntaxKind kind = token;
        NextToken();
        return FinishNode(factory.NewToken(kind), pos);
    }

    private Node ParseExpectedToken(SyntaxKind kind)
    {
        Node? tok = ParseOptionalToken(kind);
        if (tok == null)
        {
            ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_0_expected, global::Ts.Scanner.Scanner.TokenToString(kind));
            tok = FinishNode(factory.NewToken(kind), NodePos());
        }
        return tok;
    }

    private Node? ParseOptionalToken(SyntaxKind kind)
    {
        if (token == kind)
        {
            return ParseTokenNode();
        }
        return null;
    }

    private Node ParseExpectedTokenJSDoc(SyntaxKind kind)
    {
        Node? optional = ParseOptionalTokenJSDoc(kind);
        if (optional == null)
        {
            if (!Utilities.IsKeywordOrPunctuation(kind))
            {
                throw new InvalidOperationException("expected keyword or punctuation");
            }
            ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.X_0_expected, global::Ts.Scanner.Scanner.TokenToString(kind));
            optional = FinishNode(factory.NewToken(kind), NodePos());
        }
        return optional;
    }

    private Node? ParseOptionalTokenJSDoc(SyntaxKind kind)
    {
        if (token == kind)
        {
            return ParseTokenNode();
        }
        return null;
    }

    // ── node finishing ──

    private NodeList NewNodeList(TextRange loc, Node[] nodes)
    {
        NodeList list = factory.NewNodeList(nodes);
        list.Loc = loc;
        return list;
    }

    private ModifierList NewModifierList(TextRange loc, Node[] nodes)
    {
        ModifierList list = factory.NewModifierList(nodes);
        list.Loc = loc;
        return list;
    }

    private Node FinishNode(Node node, int pos) => FinishNodeWithEnd(node, pos, NodePos());

    private Node FinishNodeWithEnd(Node node, int pos, int end)
    {
        node.Loc = new TextRange(pos, end);
        node.Flags |= contextFlags;
        if (hasParseError)
        {
            node.Flags |= NodeFlags.ThisNodeHasError;
            hasParseError = false;
        }
        OverrideParentInImmediateChildren(node);
        return node;
    }

    private void OverrideParentInImmediateChildren(Node node)
    {
        currentParent = node;
        node.ForEachChild(setParentFromContext);
        currentParent = null;
    }

    // ── identifiers ──

    private Node NewIdentifier(string text)
    {
        identifierCount++;
        Node id = factory.NewIdentifier(text);
        if (text == "await")
        {
            statementHasAwaitIdentifier = true;
        }
        return id;
    }

    private string InternIdentifier(string text)
    {
        if (identifiers != null && identifiers.TryGetValue(text, out string? existing))
        {
            return existing;
        }
        string identifier = text;
        identifiers ??= new Dictionary<string, string>();
        identifiers[identifier] = identifier;
        return identifier;
    }

    private Node CreateMissingIdentifier() => FinishNode(NewIdentifier(""), NodePos());

    private Node ParsePrivateIdentifier()
    {
        int pos = NodePos();
        string text = scanner.TokenValue();
        NextToken();
        return FinishNode(factory.NewPrivateIdentifier(InternIdentifier(text)), pos);
    }

    private Node CreateIdentifier(bool isIdentifier) => CreateIdentifierWithDiagnostic(isIdentifier, null, null);

    private Node ParseIdentifierWithDiagnostic(Message? diagnosticMessage, Message? privateIdentifierDiagnosticMessage) =>
        CreateIdentifierWithDiagnostic(IsIdentifier(), diagnosticMessage, privateIdentifierDiagnosticMessage);

    private Node CreateIdentifierWithDiagnostic(bool isIdentifier, Message? diagnosticMessage, Message? privateIdentifierDiagnosticMessage)
    {
        if (isIdentifier)
        {
            int pos;
            if (scanner.HasPrecedingJSDocLeadingAsterisks())
            {
                pos = scanner.TokenStart();
            }
            else
            {
                pos = NodePos();
            }
            string text = scanner.TokenValue();
            NextTokenWithoutCheck();
            return FinishNode(NewIdentifier(InternIdentifier(text)), pos);
        }
        if (token == SyntaxKind.PrivateIdentifier)
        {
            if (privateIdentifierDiagnosticMessage != null)
            {
                ParseErrorAtCurrentToken(privateIdentifierDiagnosticMessage);
            }
            else
            {
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Private_identifiers_are_not_allowed_outside_class_bodies);
            }
            return CreateIdentifier(true);
        }
        // Only for end of file because the error gets reported incorrectly on embedded script tags.
        bool reportAtCurrentPosition = token == SyntaxKind.EndOfFile;
        if (diagnosticMessage != null)
        {
            if (reportAtCurrentPosition)
            {
                int p = scanner.TokenFullStart();
                ParseErrorAt(p, p, diagnosticMessage);
            }
            else
            {
                ParseErrorAtCurrentToken(diagnosticMessage);
            }
        }
        else if (IsReservedWord(token))
        {
            if (reportAtCurrentPosition)
            {
                int p = scanner.TokenFullStart();
                ParseErrorAt(p, p, global::Ts.Diagnostics.Diagnostics.Identifier_expected_0_is_a_reserved_word_that_cannot_be_used_here, scanner.TokenText());
            }
            else
            {
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Identifier_expected_0_is_a_reserved_word_that_cannot_be_used_here, scanner.TokenText());
            }
        }
        else
        {
            if (reportAtCurrentPosition)
            {
                int p = scanner.TokenFullStart();
                ParseErrorAt(p, p, global::Ts.Diagnostics.Diagnostics.Identifier_expected);
            }
            else
            {
                ParseErrorAtCurrentToken(global::Ts.Diagnostics.Diagnostics.Identifier_expected);
            }
        }
        return CreateMissingIdentifier();
    }

    // ── context flags ──

    private void SetContextFlags(NodeFlags flags, bool value)
    {
        if (value)
        {
            contextFlags |= flags;
        }
        else
        {
            contextFlags &= ~flags;
        }
    }

    private T DoInContext<T>(NodeFlags flags, bool value, Func<T> f)
    {
        NodeFlags saveContextFlags = contextFlags;
        SetContextFlags(flags, value);
        T result = f();
        contextFlags = saveContextFlags;
        return result;
    }

    private bool InYieldContext() => (contextFlags & NodeFlags.YieldContext) != 0;

    private bool InDisallowInContext() => (contextFlags & NodeFlags.DisallowInContext) != 0;

    private bool InDisallowConditionalTypesContext() => (contextFlags & NodeFlags.DisallowConditionalTypesContext) != 0;

    private bool InDecoratorContext() => (contextFlags & NodeFlags.DecoratorContext) != 0;

    private bool InAwaitContext() => (contextFlags & NodeFlags.AwaitContext) != 0;

    private TextRange SkipRangeTrivia(TextRange textRange) =>
        new(global::Ts.Scanner.Scanner.SkipTrivia(sourceText, textRange.Pos), textRange.End);

    // ── semicolons ──

    private bool CanParseSemicolon() =>
        token == SyntaxKind.SemicolonToken || token == SyntaxKind.CloseBraceToken ||
        token == SyntaxKind.EndOfFile || HasPrecedingLineBreak();

    private bool TryParseSemicolon()
    {
        if (!CanParseSemicolon())
        {
            return false;
        }
        if (token == SyntaxKind.SemicolonToken)
        {
            NextToken();
        }
        return true;
    }

    private bool ParseSemicolon() => TryParseSemicolon() || ParseExpected(SyntaxKind.SemicolonToken);

    private bool IsLiteralPropertyName() =>
        Utilities.TokenIsIdentifierOrKeyword(token) || token == SyntaxKind.StringLiteral ||
        token == SyntaxKind.NumericLiteral || token == SyntaxKind.BigIntLiteral;

    private static bool IsReservedWord(SyntaxKind token) =>
        SyntaxKind.FirstReservedWord <= token && token <= SyntaxKind.LastReservedWord;

    private static Diagnostic[] AttachFileToDiagnostics(Diagnostic[] diags, SourceFile file)
    {
        foreach (Diagnostic d in diags)
        {
            d.SetFile(file);
            Diagnostic[]? related = d.RelatedInformation();
            if (related != null)
            {
                foreach (Diagnostic r in related)
                {
                    r.SetFile(file);
                }
            }
        }
        return diags;
    }

    private SyntaxKind ReScanLessThanToken()
    {
        token = scanner.ReScanLessThanToken();
        return token;
    }

    private SyntaxKind ReScanGreaterThanToken()
    {
        token = scanner.ReScanGreaterThanToken();
        return token;
    }
}
