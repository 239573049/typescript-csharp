using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Collections;
using Ts.Core;
using Ts.TsPath;

namespace Ts.Ast;

// Go: internal/ast/ast.go — the SourceFile node (the only handWritten node, plan/02 §12)
// and its neighbouring region types (CommentRange/FileReference/PatternAmbientModule/
// CheckJsDirective/SourceFileLike/Pragma family, ast.go:2376-3050).
//
// SCOPE (deferred to later in phase 3 / phase 4): the language-service and parser-hook
// surface that depends on not-yet-ported machinery is omitted here:
//   - resolveJSDoc + the parseJSDocForNode parser hook (ast.go:2612-2637)
//   - VisitEachChild / UpdateSourceFile (ast.go:2651-2653, 2691-2698) — needs the
//     NodeVisitor visitToken/visitTopLevelStatements surface
//   - GetNameTable (ast.go:2718-2747) — needs Node.JSDoc + literalIsName/IsTagName
//   - GetOrCreateToken / createToken + token cache (ast.go:2774-2836)
//   - IsJS (ast.go:2655-2657) — needs IsSourceFileJS (utilities.go)
//   - the SourceFileDataKey external-lazy-data mechanism (ast.go:2402-2447)
//   - GetPositionMap / positionMap — dropped entirely (positionmap.go not ported,
//     plan/06 §一.6 / task scope)

// ast.go:2396-2400
public struct SourceFileMetaData
{
    public string PackageJsonType;
    public string PackageJsonDirectory;
    public ResolutionMode ImpliedNodeFormat;
}

// ast.go:2449-2452
public sealed class CheckJsDirective
{
    public bool Enabled;
    public CommentRange Range;
}

// ast.go:2974-2977
public interface SourceFileLike
{
    string Text();
    int[] ECMALineMap();
}

// ast.go:2979-2983 — embeds core.TextRange (promoted Pos/End).
public struct CommentRange
{
    public TextRange TextRange;
    public SyntaxKind Kind;
    public bool HasTrailingNewLine;

    public int Pos => TextRange.Pos;
    public int End => TextRange.End;
    public int Len => TextRange.Len;
}

// ast.go:2993-2998 — embeds core.TextRange.
public struct FileReference
{
    public TextRange TextRange;
    public string FileName;
    public ResolutionMode ResolutionMode;
    public bool Preserve;

    public int Pos => TextRange.Pos;
    public int End => TextRange.End;
}

// ast.go:3000-3004 — embeds core.TextRange.
public struct PragmaArgument
{
    public TextRange TextRange;
    public string Name;
    public string Value;
}

// ast.go:3006-3010 — embeds CommentRange.
public struct Pragma
{
    public CommentRange CommentRange;
    public string Name;
    public Dictionary<string, PragmaArgument> Args;
}

// ast.go:3012-3021 — `type PragmaKindFlags = uint8`.
[Flags]
public enum PragmaKindFlags : byte
{
    None = 0, // PragmaKindFlagsNone
    TripleSlashXML = 1 << 0,
    SingleLine = 1 << 1,
    MultiLine = 1 << 2,
    All = TripleSlashXML | SingleLine | MultiLine,
    Default = All,
}

// ast.go:3023-3027
public struct PragmaArgumentSpecification
{
    public string Name;
    public bool Optional;
    public bool CaptureSpan;
}

// ast.go:3028-3036
public struct PragmaSpecification
{
    public PragmaArgumentSpecification[]? Args;
    public PragmaKindFlags Kind;

    public bool IsTripleSlash() => (Kind & PragmaKindFlags.TripleSlashXML) != 0;
}

// ast.go:2376-2379
public sealed class PatternAmbientModule
{
    public Pattern Pattern;
    public Symbol? Symbol;
}

// ast.go:2464-2544
public sealed partial class SourceFile : Node, DeclarationBase, LocalsContainerBase, CompositeBase, SourceFileLike
{
    // Fields set by NewSourceFile
    internal string fileName = ""; // For debugging convenience
    internal SourceFileParseOptions parseOptions;
    internal string text = "";
    public StatementList? Statements; // NodeList[*Statement]
    public TokenNode? EndOfFileToken; // TokenNode[*EndOfFileToken]

    // Fields set by parser
    internal Diagnostic[]? diagnostics;
    internal Diagnostic[]? jsDiagnostics;
    internal Diagnostic[]? jsdocDiagnostics;
    public LanguageVariant LanguageVariant;
    public ScriptKind ScriptKind;
    public bool IsDeclarationFile;
    public bool ContainsNonASCII;
    public Tristate UsesUriStyleNodeCoreModules;
    public Dictionary<string, string>? Identifiers;
    public int IdentifierCount;
    internal Node[]? imports; // []LiteralLikeNode
    public Node[]? ModuleAugmentations; // []ModuleName
    public string[]? AmbientModuleNames;
    public CommentDirective[]? CommentDirectives;
    internal Dictionary<Node, Node[]>? jsdocCache;
    internal bool hasLazyJSDoc;
    public Node[]? ReparsedClones;
    public Pragma[]? Pragmas;
    public FileReference[]? ReferencedFiles;
    public FileReference[]? TypeReferenceDirectives;
    public FileReference[]? LibReferenceDirectives;
    public CheckJsDirective? CheckJsDirective;
    public int NodeCount;
    public int TextCount;
    public Node? CommonJSModuleIndicator;
    // If this is the SourceFile itself, then this module was "forced"
    // to be an external module (previously "true").
    public Node? ExternalModuleIndicator;

    // Fields set by binder
    internal bool isBound;
    private bool bindDone;
    private readonly Lock bindLock = new();
    internal Diagnostic[]? bindDiagnostics;
    public Diagnostic[]? BindSuggestionDiagnostics;
    public FlowNode? EndFlowNode;
    public int SymbolCount;
    public Set<string> ClassifiableNames = new();
    public PatternAmbientModule[]? PatternAmbientModules;
    public SymbolTable? GlobalExports;

    // Fields set by ECMALineMap
    internal int[]? ecmaLineMap;
    private readonly Lock ecmaLineMapMu = new();

    // ast.go:2535-2536 — declarationMapMu/declarationMap. Guarded by the mutex; the
    // mutex is NOT reentrant in Go, so callers must not recurse through GetDeclarationMap
    // (computeDeclarationMap itself never does).
    private readonly Lock declarationMapMu = new();
    private Dictionary<string, List<Node>>? declarationMap;

    // Fields set by language service
    public UInt128 Hash;

    // ── DeclarationBase / LocalsContainerBase / CompositeBase mixin surface ──
    public Symbol? Symbol { get; set; } // DeclarationBase
    public SymbolTable? Locals { get; set; } // LocalsContainerBase
    public Node? NextContainer { get; set; } // LocalsContainerBase
    private uint facts; // CompositeBase (private; atomic via Volatile in CachedSubtreeFacts)

    public override DeclarationBase? DeclarationData() => this;
    public override LocalsContainerBase? LocalsContainerData() => this;
    internal override SubtreeFacts SubtreeFactsWorker() => CachedSubtreeFacts(ref facts); // CompositeBase (ast.go:1599-1607)

    // ast.go:2560-2562
    public SourceFileParseOptions ParseOptions() => parseOptions;

    // ast.go:2564-2566
    public string Text() => text;

    // ast.go:2568-2570
    public string FileName() => parseOptions.FileName;

    // ast.go:2572-2574
    public Path Path() => parseOptions.Path;

    // ast.go:2576-2578
    public Node[]? Imports() => imports;

    // ast.go:2580-2586
    public Diagnostic[]? Diagnostics() => diagnostics;

    public void SetDiagnostics(Diagnostic[]? diags) => diagnostics = diags;

    // ast.go:2588-2594
    public Diagnostic[]? JSDiagnostics() => jsDiagnostics;

    public void SetJSDiagnostics(Diagnostic[]? diags) => jsDiagnostics = diags;

    // ast.go:2596-2602
    public Diagnostic[]? JSDocDiagnostics() => jsdocDiagnostics;

    public void SetJSDocDiagnostics(Diagnostic[]? diags) => jsdocDiagnostics = diags;

    // ast.go:2604-2606
    public void SetJSDocCache(Dictionary<Node, Node[]>? cache) => jsdocCache = cache;

    // ast.go:2608-2610
    public void SetHasLazyJSDoc(bool lazy) => hasLazyJSDoc = lazy;

    // ast.go:2639-2645
    public Diagnostic[]? BindDiagnostics() => bindDiagnostics;

    public void SetBindDiagnostics(Diagnostic[]? diags) => bindDiagnostics = diags;

    // ast.go:2647-2649
    public override bool ForEachChild(Visitor v)
    {
        return VisitNodeList(v, Statements) || Visit(v, EndOfFileToken);
    }

    // ast.go:2655-2657
    public bool IsJS()
    {
        return AstUtilities.IsSourceFileJS(this);
    }

    // ast.go:2659-2678
    internal void CopyFrom(SourceFile other)
    {
        // Do not copy fields set by NewSourceFile (Text, FileName, Path, or Statements)
        LanguageVariant = other.LanguageVariant;
        ScriptKind = other.ScriptKind;
        IsDeclarationFile = other.IsDeclarationFile;
        ContainsNonASCII = other.ContainsNonASCII;
        UsesUriStyleNodeCoreModules = other.UsesUriStyleNodeCoreModules;
        Identifiers = other.Identifiers;
        imports = other.imports;
        ModuleAugmentations = other.ModuleAugmentations;
        AmbientModuleNames = other.AmbientModuleNames;
        CommentDirectives = other.CommentDirectives;
        Pragmas = other.Pragmas;
        ReferencedFiles = other.ReferencedFiles;
        TypeReferenceDirectives = other.TypeReferenceDirectives;
        LibReferenceDirectives = other.LibReferenceDirectives;
        CommonJSModuleIndicator = other.CommonJSModuleIndicator;
        ExternalModuleIndicator = other.ExternalModuleIndicator;
        Flags |= other.Flags;
    }

    // ast.go:2680-2685
    public override Node? Clone(NodeFactoryCoercible f)
    {
        var updated = f.AsNodeFactory().NewSourceFile(parseOptions, text, Statements, EndOfFileToken);
        var newFile = updated.AsSourceFile();
        newFile.CopyFrom(this);
        return NodeFactory.CloneNode(updated, AsNode(), f.AsNodeFactory().hooks);
    }

    // ast.go:2687-2689
    internal override SubtreeFacts ComputeSubtreeFacts()
    {
        return PropagateNodeListSubtreeFacts(Statements, PropagateSubtreeFacts);
    }

    // ast.go:2700-2714 — RWMutex + double-check → lock-free read via Volatile + lock.
    public int[] ECMALineMap()
    {
        var lineMap = Volatile.Read(ref ecmaLineMap);
        if (lineMap == null)
        {
            lock (ecmaLineMapMu)
            {
                lineMap = ecmaLineMap;
                if (lineMap == null)
                {
                    lineMap = CoreUtil.ComputeECMALineStarts(Text());
                    Volatile.Write(ref ecmaLineMap, lineMap);
                }
            }
        }
        return lineMap;
    }

    // ast.go:2749-2751
    public bool IsBound() => Volatile.Read(ref isBound);

    // ast.go:2838-2846 — GetDeclarationMap. Go's sync.Mutex maps to a lock; the map is
    // built once on first use (the mutex is not reentrant, and computeDeclarationMap
    // never re-enters).
    public Dictionary<string, List<Node>> GetDeclarationMap()
    {
        lock (declarationMapMu)
        {
            declarationMap ??= ComputeDeclarationMap();
            return declarationMap;
        }
    }

    // ast.go:2848-2955 — computeDeclarationMap.
    private Dictionary<string, List<Node>> ComputeDeclarationMap()
    {
        var result = new Dictionary<string, List<Node>>();

        void AddDeclaration(Node declaration)
        {
            var name = AstUtilities.GetDeclarationName(declaration);
            if (name != "")
            {
                if (!result.TryGetValue(name, out var declarations))
                {
                    declarations = [];
                    result[name] = declarations;
                }
                declarations.Add(declaration);
            }
        }

        bool Visit(Node node)
        {
            switch (node.Kind)
            {
                case SyntaxKind.FunctionDeclaration:
                case SyntaxKind.FunctionExpression:
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.MethodSignature:
                    {
                        var declarationName = AstUtilities.GetDeclarationName(node);
                        if (declarationName != "")
                        {
                            var declarations = result.GetValueOrDefault(declarationName);
                            Node? lastDeclaration = null;
                            if (declarations != null && declarations.Count != 0)
                            {
                                lastDeclaration = declarations[^1];
                            }
                            // Check whether this declaration belongs to an "overload group".
                            if (lastDeclaration != null && node.Parent == lastDeclaration.Parent && node.Symbol() == lastDeclaration.Symbol())
                            {
                                // Overwrite the last declaration if it was an overload and this one is an implementation.
                                if (node.Body() != null && lastDeclaration.Body() == null)
                                {
                                    declarations![^1] = node;
                                }
                            }
                            else
                            {
                                if (declarations == null)
                                {
                                    declarations = [];
                                    result[declarationName] = declarations;
                                }
                                declarations.Add(node);
                            }
                        }
                        node.ForEachChild(Visit);
                        break;
                    }
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.ClassExpression:
                case SyntaxKind.InterfaceDeclaration:
                case SyntaxKind.TypeAliasDeclaration:
                case SyntaxKind.EnumDeclaration:
                case SyntaxKind.ModuleDeclaration:
                case SyntaxKind.ImportEqualsDeclaration:
                case SyntaxKind.ImportClause:
                case SyntaxKind.NamespaceImport:
                case SyntaxKind.GetAccessor:
                case SyntaxKind.SetAccessor:
                case SyntaxKind.TypeLiteral:
                    AddDeclaration(node);
                    node.ForEachChild(Visit);
                    break;
                case SyntaxKind.ImportSpecifier:
                case SyntaxKind.ExportSpecifier:
                    if (node.PropertyName() != null)
                    {
                        AddDeclaration(node);
                    }
                    break;
                case SyntaxKind.Parameter:
                    // Only consider parameter properties
                    if (!AstUtilities.HasSyntacticModifier(node, ModifierFlags.ParameterPropertyModifier))
                    {
                        break;
                    }
                    goto case SyntaxKind.VariableDeclaration;
                case SyntaxKind.VariableDeclaration:
                case SyntaxKind.BindingElement:
                    {
                        var name = node.Name();
                        if (name != null)
                        {
                            if (AstUtilities.IsBindingPattern(name))
                            {
                                node.Name()!.ForEachChild(Visit);
                            }
                            else
                            {
                                if (node.Initializer() != null)
                                {
                                    Visit(node.Initializer()!);
                                }
                                AddDeclaration(node);
                            }
                        }
                        break;
                    }
                case SyntaxKind.EnumMember:
                case SyntaxKind.PropertyDeclaration:
                case SyntaxKind.PropertySignature:
                    AddDeclaration(node);
                    break;
                case SyntaxKind.ExportDeclaration:
                    {
                        // Handle named exports case e.g.:
                        //    export {a, b as B} from "mod";
                        var exportClause = node.AsExportDeclaration().ExportClause;
                        if (exportClause != null)
                        {
                            if (NodeTests.IsNamedExports(exportClause))
                            {
                                foreach (var element in exportClause.Elements()!)
                                {
                                    Visit(element);
                                }
                            }
                            else
                            {
                                Visit(exportClause.AsNamespaceExport().Name()!);
                            }
                        }
                        break;
                    }
                case SyntaxKind.ImportDeclaration:
                    {
                        var importClause = node.AsImportDeclaration().ImportClause;
                        if (importClause != null)
                        {
                            // Handle default import case e.g.:
                            //    import d from "mod";
                            if (importClause.Name() != null)
                            {
                                AddDeclaration(importClause.Name()!);
                            }
                            // Handle named bindings in imports e.g.:
                            //    import * as NS from "mod";
                            //    import {a, b as B} from "mod";
                            var namedBindings = importClause.AsImportClause().NamedBindings;
                            if (namedBindings != null)
                            {
                                if (namedBindings.Kind == SyntaxKind.NamespaceImport)
                                {
                                    AddDeclaration(namedBindings);
                                }
                                else
                                {
                                    foreach (var element in namedBindings.Elements()!)
                                    {
                                        Visit(element);
                                    }
                                }
                            }
                        }
                        break;
                    }
                case SyntaxKind.BinaryExpression:
                    switch (AstUtilities.GetAssignmentDeclarationKind(node))
                    {
                        case JSDeclarationKind.ExportsProperty:
                        case JSDeclarationKind.ThisProperty:
                        case JSDeclarationKind.Property:
                            AddDeclaration(node);
                            break;
                    }
                    node.ForEachChild(Visit);
                    break;
                default:
                    node.ForEachChild(Visit);
                    break;
            }
            return false;
        }

        ForEachChild(Visit);
        return result;
    }

    // ast.go:2765-2770 — sync.Once → double-checked lock; isBound stored last.
    public void BindOnce(Action bind)
    {
        lock (bindLock)
        {
            if (!bindDone)
            {
                bind();
                Volatile.Write(ref isBound, true);
                bindDone = true;
            }
        }
    }
}

// ast.go:2546-2558, 2691 — hand-written NodeFactory members for the handWritten SourceFile.
public sealed partial class NodeFactory
{
    // ast.go:2546-2558
    public Node NewSourceFile(SourceFileParseOptions opts, string text, StatementList? statements, TokenNode? endOfFileToken)
    {
        if (Ts.TsPath.TsPath.GetEncodedRootLength(opts.FileName) == 0 || opts.FileName != Ts.TsPath.TsPath.NormalizePath(opts.FileName))
        {
            throw new InvalidOperationException($"fileName should be normalized and absolute: \"{opts.FileName}\"");
        }
        var data = new SourceFile
        {
            fileName = opts.FileName,
            parseOptions = opts,
            text = text,
            ContainsNonASCII = Ts.StringUtil.StringUtil.ContainsNonASCII(text),
            Statements = statements,
            EndOfFileToken = endOfFileToken,
        };
        return NewNode(SyntaxKind.SourceFile, data);
    }

    // ast.go:2985-2991
    public CommentRange NewCommentRange(SyntaxKind kind, int pos, int end, bool hasTrailingNewLine)
    {
        return new CommentRange
        {
            TextRange = new TextRange(pos, end),
            Kind = kind,
            HasTrailingNewLine = hasTrailingNewLine,
        };
    }
}
