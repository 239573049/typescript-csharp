// Ported from tsgo internal/testutil/tsbaseline/type_symbol_baseline.go.
//
// This is the byte-load-bearing .types/.symbols writer. It preserves the Go walk order,
// source-text slicing, CRLF layout, blank-line suppression around braces, five-declaration
// symbol cap, and zero-based UTF-16 Decl(file, line, col) coordinates.
//
// The production Checker surface is still incomplete: GetTypeAtLocation is absent and
// GetSymbolAtLocation is a throwing internal stub. This test-only writer therefore reaches the
// already-ported internal type-query leaves through UnsafeAccessor (no reflection), and catches
// unsupported nodes individually. A failed type query emits Go's error-type display fallback
// (`any`); a failed symbol query uses a best-effort bound symbol or omits the node. Every such
// fallback is counted, so the conformance runner can force the artifact to remain allowlisted
// even if its approximate bytes happen to equal the reference.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using global::Ts.Ast;

namespace Ts.TestUtil;

public sealed record TypeSymbolBaselineEntry(
    int Line,
    string SourceText,
    string Text,
    string Underline = "");

public sealed record TypeSymbolBaselineGeneration(
    string Types,
    string Symbols,
    int TypeQueryFailures,
    int SymbolQueryFailures,
    IReadOnlyList<string> QueryFailureExamples);

public static partial class TypeSymbolBaseline
{
    private const string HarnessNewLine = "\r\n";
    private const int MaximumFailureExamples = 8;

    public static TypeSymbolBaselineGeneration Run(
        global::Ts.TestUtil.IBaselineTestContext typeContext,
        global::Ts.TestUtil.IBaselineTestContext symbolContext,
        string baselinePath,
        string header,
        global::Ts.Compiler.Program program,
        IReadOnlyList<global::Ts.TestUtil.BaselineTestFile> allFiles,
        global::Ts.TestUtil.Baseline.Options options,
        bool hasErrorBaseline)
    {
        TypeSymbolBaselineGeneration generation = Generate(
            header,
            program,
            allFiles,
            hasErrorBaseline);

        string typeOutputName = TsExtensionRegex().Replace(Path.GetFileName(baselinePath), ".types");
        string symbolOutputName = TsExtensionRegex().Replace(Path.GetFileName(baselinePath), ".symbols");

        global::Ts.TestUtil.Baseline.Run(
            typeContext,
            typeOutputName,
            generation.Types,
            CopyOptions(options, FixupOldTypeBaseline));
        global::Ts.TestUtil.Baseline.Run(symbolContext, symbolOutputName, generation.Symbols, options);
        return generation;
    }

    public static TypeSymbolBaselineGeneration Generate(
        string header,
        global::Ts.Compiler.Program program,
        IReadOnlyList<global::Ts.TestUtil.BaselineTestFile> allFiles,
        bool hasErrorBaseline)
    {
        var walker = new TypeWriterWalker(program, hasErrorBaseline);
        string types = GenerateBaseline(
            allFiles,
            header,
            isSymbolBaseline: false,
            file => walker.GetTypes(file.UnitName));
        string symbols = GenerateBaseline(
            allFiles,
            header,
            isSymbolBaseline: true,
            file => walker.GetSymbols(file.UnitName));

        return new TypeSymbolBaselineGeneration(
            types,
            symbols,
            walker.TypeQueryFailures,
            walker.SymbolQueryFailures,
            walker.QueryFailureExamples);
    }

    // Public because the harness tests exercise the byte formatter independently from the
    // concurrently moving Checker implementation. Production callers use Generate/Run above.
    public static string GenerateBaseline(
        IReadOnlyList<global::Ts.TestUtil.BaselineTestFile> allFiles,
        string header,
        bool isSymbolBaseline,
        Func<global::Ts.TestUtil.BaselineTestFile, IReadOnlyList<TypeSymbolBaselineEntry>> getResults)
    {
        var result = new StringBuilder();
        IReadOnlyList<string> baselines = IterateBaseline(allFiles, getResults, isSymbolBaseline);
        foreach (string value in baselines)
        {
            result.Append(value);
        }

        if (result.Length == 0)
        {
            return global::Ts.TestUtil.Baseline.NoContent;
        }
        return "//// [" + header + "] ////" + HarnessNewLine + HarnessNewLine + result;
    }

    public static string FixupOldTypeBaseline(string value)
    {
        var result = new StringBuilder(value.Length);
        bool performanceStats = false;
        foreach (string rawLine in value.Split('\n'))
        {
            string line = rawLine;
            if (IsTypeBaselineNodeReuseLine(line))
            {
                continue;
            }

            if (!performanceStats && line.StartsWith("=== Performance Stats ===", StringComparison.Ordinal))
            {
                performanceStats = true;
                continue;
            }
            if (performanceStats)
            {
                if (line.StartsWith("=== ", StringComparison.Ordinal))
                {
                    performanceStats = false;
                }
                else
                {
                    continue;
                }
            }

            const string relativePrefixNew = "=== ";
            const string relativePrefixOld = relativePrefixNew + "./";
            if (line.StartsWith(relativePrefixOld, StringComparison.Ordinal))
            {
                line = relativePrefixNew + line[relativePrefixOld.Length..];
            }

            result.Append(line);
            result.Append('\n');
        }

        return result.Length == 0 ? "" : result.ToString(0, result.Length - 1);
    }

    private static bool IsTypeBaselineNodeReuseLine(string value)
    {
        if (!value.StartsWith('>'))
        {
            return false;
        }

        // Faithful to Go: CutPrefix(">"), then discard one more character before TrimLeft.
        string line = value[1..];
        if (line.Length == 0)
        {
            return false;
        }
        line = line[1..].TrimStart(' ');
        if (!line.StartsWith(':'))
        {
            return false;
        }

        foreach (char c in line[1..])
        {
            if (c != ' ' && c != '^' && c != '\r')
            {
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<string> IterateBaseline(
        IReadOnlyList<global::Ts.TestUtil.BaselineTestFile> allFiles,
        Func<global::Ts.TestUtil.BaselineTestFile, IReadOnlyList<TypeSymbolBaselineEntry>> getResults,
        bool isSymbolBaseline)
    {
        var baselines = new List<string>();
        foreach (global::Ts.TestUtil.BaselineTestFile file in allFiles)
        {
            var typeLines = new StringBuilder();
            typeLines.Append("=== ");
            typeLines.Append(file.UnitName);
            typeLines.Append(" ===");
            typeLines.Append(HarnessNewLine);

            string[] codeLines = CodeLinesRegex().Split(file.Content);
            IReadOnlyList<TypeSymbolBaselineEntry> results = getResults(file);
            int lastIndexWritten = -1;
            foreach (TypeSymbolBaselineEntry entry in results)
            {
                if (isSymbolBaseline && entry.Text.Length == 0)
                {
                    return baselines;
                }

                if (lastIndexWritten == -1)
                {
                    AppendJoinedLines(typeLines, codeLines, 0, entry.Line + 1);
                    typeLines.Append(HarnessNewLine);
                }
                else if (lastIndexWritten != entry.Line)
                {
                    int nextLine = lastIndexWritten + 1;
                    if (!(nextLine < codeLines.Length &&
                        (IsBracketLine(codeLines[nextLine]) || string.IsNullOrWhiteSpace(codeLines[nextLine]))))
                    {
                        typeLines.Append(HarnessNewLine);
                    }
                    AppendJoinedLines(typeLines, codeLines, nextLine, entry.Line + 1);
                    typeLines.Append(HarnessNewLine);
                }

                lastIndexWritten = entry.Line;
                string lineText = LineEndRegex().Replace(entry.SourceText, "");
                typeLines.Append('>');
                typeLines.Append(lineText);
                typeLines.Append(" : ");
                typeLines.Append(entry.Text);
                typeLines.Append(HarnessNewLine);

                // PORT-NOTE: deliberately dormant. Go pads this line by UTF-8 byte length, but
                // type_symbol_baseline.go never populates underline. Per plan/12, the byte-pad
                // compatibility path must not be ported unless upstream activates underlining.
                if (entry.Underline.Length != 0)
                {
                    throw new InvalidOperationException(
                        "type baseline underline output is dormant upstream and intentionally unsupported");
                }
            }

            if (lastIndexWritten + 1 < codeLines.Length)
            {
                int nextLine = lastIndexWritten + 1;
                if (!(nextLine < codeLines.Length &&
                    (IsBracketLine(codeLines[nextLine]) || string.IsNullOrWhiteSpace(codeLines[nextLine]))))
                {
                    typeLines.Append(HarnessNewLine);
                }
                AppendJoinedLines(typeLines, codeLines, nextLine, codeLines.Length);
            }
            typeLines.Append(HarnessNewLine);

            baselines.Add(RemoveTestPathPrefixes(typeLines.ToString()));
        }
        return baselines;
    }

    private static void AppendJoinedLines(
        StringBuilder builder,
        IReadOnlyList<string> lines,
        int start,
        int endExclusive)
    {
        for (int index = start; index < endExclusive; index++)
        {
            if (index != start)
            {
                builder.Append(HarnessNewLine);
            }
            builder.Append(lines[index]);
        }
    }

    private static bool IsBracketLine(string line)
    {
        // Go regexp \s is ASCII-only. Do not use .NET's broader Unicode \s here.
        ReadOnlySpan<char> value = line.AsSpan().Trim(" \t\r\n\f");
        return value.Length == 1 && (value[0] == '{' || value[0] == '|' || value[0] == '}');
    }

    private static global::Ts.TestUtil.Baseline.Options CopyOptions(
        global::Ts.TestUtil.Baseline.Options options,
        Func<string, string> typeFixup)
    {
        Func<string, string>? oldFixup = options.DiffFixupOld;
        return new global::Ts.TestUtil.Baseline.Options
        {
            Subfolder = options.Subfolder,
            IsSubmodule = options.IsSubmodule,
            IsSubmoduleAccepted = options.IsSubmoduleAccepted,
            IsSubmoduleTriaged = options.IsSubmoduleTriaged,
            DiffFixupOld = oldFixup is null
                ? typeFixup
                : value => typeFixup(oldFixup(value)),
            DiffFixupNew = options.DiffFixupNew,
            SkipDiffWithOld = options.SkipDiffWithOld,
        };
    }

    private static string RemoveTestPathPrefixes(string text) => text
        .Replace("/.ts/", "", StringComparison.Ordinal)
        .Replace("/.lib/", "", StringComparison.Ordinal)
        .Replace("/.src/", "", StringComparison.Ordinal)
        .Replace("bundled:///libs/", "", StringComparison.Ordinal)
        .Replace("file:///./ts/", "file:///", StringComparison.Ordinal)
        .Replace("file:///./lib/", "file:///", StringComparison.Ordinal)
        .Replace("file:///./src/", "file:///", StringComparison.Ordinal);

    private static bool IsDefaultLibraryFile(string filePath)
    {
        string fileName = global::Ts.TsPath.TsPath.GetBaseFileName(filePath);
        return fileName.StartsWith("lib.", StringComparison.Ordinal) &&
            fileName.EndsWith(global::Ts.TsPath.TsPath.ExtensionDts, StringComparison.Ordinal);
    }

    [GeneratedRegex("[\\r\\u2028\\u2029]|\\r?\\n")]
    private static partial Regex CodeLinesRegex();

    [GeneratedRegex("\\r?\\n")]
    private static partial Regex LineEndRegex();

    [GeneratedRegex("\\.tsx?$")]
    private static partial Regex TsExtensionRegex();

    private sealed class TypeWriterWalker
    {
        private readonly global::Ts.Compiler.Program program;
        private readonly bool hadErrorBaseline;
        private readonly List<string> queryFailureExamples = [];
        private global::Ts.Ast.SourceFile? currentSourceFile;

        public TypeWriterWalker(global::Ts.Compiler.Program program, bool hadErrorBaseline)
        {
            this.program = program;
            this.hadErrorBaseline = hadErrorBaseline;
        }

        public int TypeQueryFailures { get; private set; }

        public int SymbolQueryFailures { get; private set; }

        public IReadOnlyList<string> QueryFailureExamples => queryFailureExamples;

        public IReadOnlyList<TypeSymbolBaselineEntry> GetTypes(string fileName)
        {
            currentSourceFile = program.GetSourceFile(fileName);
            if (currentSourceFile is null)
            {
                RecordFailure(isSymbolWalk: false, null, new InvalidOperationException(
                    "Program.GetSourceFile returned null for " + fileName));
                return [];
            }
            return VisitNode(currentSourceFile.AsNode(), isSymbolWalk: false);
        }

        public IReadOnlyList<TypeSymbolBaselineEntry> GetSymbols(string fileName)
        {
            currentSourceFile = program.GetSourceFile(fileName);
            if (currentSourceFile is null)
            {
                RecordFailure(isSymbolWalk: true, null, new InvalidOperationException(
                    "Program.GetSourceFile returned null for " + fileName));
                return [];
            }
            return VisitNode(currentSourceFile.AsNode(), isSymbolWalk: true);
        }

        private IReadOnlyList<TypeSymbolBaselineEntry> VisitNode(
            global::Ts.Ast.Node node,
            bool isSymbolWalk)
        {
            IReadOnlyList<global::Ts.Ast.Node> nodes = ForEachAstNode(node);
            var results = new List<TypeSymbolBaselineEntry>();
            foreach (global::Ts.Ast.Node current in nodes)
            {
                if (global::Ts.Ast.AstUtilities.IsExpressionNode(current) ||
                    current.Kind == global::Ts.Ast.SyntaxKind.Identifier ||
                    IsDeclarationName(current))
                {
                    TypeSymbolBaselineEntry? entry = WriteTypeOrSymbol(current, isSymbolWalk);
                    if (entry is not null)
                    {
                        results.Add(entry);
                    }
                }
            }
            return results;
        }

        private static IReadOnlyList<global::Ts.Ast.Node> ForEachAstNode(global::Ts.Ast.Node node)
        {
            var result = new List<global::Ts.Ast.Node>();
            var work = new List<global::Ts.Ast.Node> { node };
            var children = new List<global::Ts.Ast.Node>();

            while (work.Count != 0)
            {
                int last = work.Count - 1;
                global::Ts.Ast.Node element = work[last];
                work.RemoveAt(last);
                global::Ts.Ast.Node? parent = element.Parent;

                if ((element.Flags & global::Ts.Ast.NodeFlags.Reparsed) == 0 ||
                    element.Kind == global::Ts.Ast.SyntaxKind.AsExpression ||
                    element.Kind == global::Ts.Ast.SyntaxKind.SatisfiesExpression ||
                    (parent is not null &&
                        (parent.Kind == global::Ts.Ast.SyntaxKind.SatisfiesExpression ||
                            parent.Kind == global::Ts.Ast.SyntaxKind.AsExpression) &&
                        element == parent.Expression()))
                {
                    if ((element.Flags & global::Ts.Ast.NodeFlags.Reparsed) == 0 ||
                        (parent is not null &&
                            (parent.Kind == global::Ts.Ast.SyntaxKind.AsExpression ||
                                parent.Kind == global::Ts.Ast.SyntaxKind.SatisfiesExpression)))
                    {
                        result.Add(element);
                    }

                    element.ForEachChild(child =>
                    {
                        children.Add(child);
                        return false;
                    });
                    children.Reverse();
                    work.AddRange(children);
                    children.Clear();
                }
            }
            return result;
        }

        private TypeSymbolBaselineEntry? WriteTypeOrSymbol(
            global::Ts.Ast.Node node,
            bool isSymbolWalk)
        {
            global::Ts.Ast.SourceFile sourceFile = currentSourceFile!;
            int actualPosition = global::Ts.Scanner.Scanner.SkipTrivia(sourceFile.Text(), node.Pos);
            int line = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, actualPosition);
            string sourceText = global::Ts.Scanner.Scanner.GetSourceTextOfNodeFromSourceFile(
                sourceFile,
                node,
                includeTrivia: false);

            (global::Ts.Checker.Checker checker, Action release) =
                program.GetTypeCheckerForFile(default, sourceFile);
            try
            {
                if (!isSymbolWalk)
                {
                    return WriteType(checker, node, line, sourceText);
                }
                return WriteSymbol(checker, node, line, sourceText);
            }
            finally
            {
                release();
            }
        }

        private TypeSymbolBaselineEntry? WriteType(
            global::Ts.Checker.Checker checker,
            global::Ts.Ast.Node node,
            int line,
            string sourceText)
        {
            global::Ts.Ast.Node? parent = node.Parent;
            if (parent is null)
            {
                return null;
            }

            // Don't try to get the type of something that's already a type. Exception for `T`
            // in `type T = something` because that may evaluate to some interesting type.
            if (global::Ts.Ast.AstUtilities.IsPartOfTypeNode(node) ||
                ((node.Kind == global::Ts.Ast.SyntaxKind.AsExpression ||
                    node.Kind == global::Ts.Ast.SyntaxKind.SatisfiesExpression) &&
                    node.Type() is { } reparsedType &&
                    (reparsedType.Flags & global::Ts.Ast.NodeFlags.Reparsed) != 0) ||
                (global::Ts.Ast.NodeTests.IsIdentifier(node) &&
                    !DeclarationHasValueMeaning(parent) &&
                    !(IsTypeOrJsTypeAliasDeclaration(parent) && parent.Name() == node)))
            {
                return null;
            }

            if (global::Ts.Ast.NodeTests.IsOmittedExpression(node))
            {
                return null;
            }

            global::Ts.Checker.Type? type;
            try
            {
                type = GetTypeAtLocation(checker, node);
            }
            catch (Exception exception)
            {
                RecordFailure(isSymbolWalk: false, node, exception);
                return new TypeSymbolBaselineEntry(line, sourceText, "any");
            }

            try
            {
                string typeString;
                if (!hadErrorBaseline &&
                    (type.Flags & global::Ts.Checker.TypeFlags.Any) != 0 &&
                    !global::Ts.Ast.NodeTests.IsBindingElement(parent) &&
                    !global::Ts.Ast.AstUtilities.IsPropertyAccessOrQualifiedName(parent) &&
                    !IsLabelName(node) &&
                    !global::Ts.Ast.AstUtilities.IsGlobalScopeAugmentation(parent) &&
                    !global::Ts.Ast.NodeTests.IsMetaProperty(parent) &&
                    !IsImportStatementName(node) &&
                    !IsExportStatementName(node) &&
                    !IsIntrinsicJsxTag(node, currentSourceFile!))
                {
                    typeString = type.AsIntrinsicType().IntrinsicName();
                }
                else
                {
                    const global::Ts.Checker.TypeFormatFlags flags =
                        global::Ts.Checker.TypeFormatFlags.NoTruncation |
                        global::Ts.Checker.TypeFormatFlags.AllowUniqueESSymbolType |
                        global::Ts.Checker.TypeFormatFlags.GenerateNamesForShadowedTypeParams;
                    typeString = CheckerAccess.TypeToStringEx(checker, type, parent, flags, null);
                    if (global::Ts.Ast.NodeTests.IsIdentifier(node) &&
                        parent.Kind == global::Ts.Ast.SyntaxKind.TypeAliasDeclaration &&
                        parent.Name() == node &&
                        string.Equals(typeString, node.Text(), StringComparison.Ordinal))
                    {
                        typeString = CheckerAccess.TypeToStringEx(
                            checker,
                            type,
                            parent,
                            flags | global::Ts.Checker.TypeFormatFlags.InTypeAlias,
                            null);
                    }
                }
                return new TypeSymbolBaselineEntry(line, sourceText, typeString);
            }
            catch (Exception exception)
            {
                RecordFailure(isSymbolWalk: false, node, exception);
                return new TypeSymbolBaselineEntry(line, sourceText, "any");
            }
        }

        private TypeSymbolBaselineEntry? WriteSymbol(
            global::Ts.Checker.Checker checker,
            global::Ts.Ast.Node node,
            int line,
            string sourceText)
        {
            global::Ts.Ast.Symbol? symbol;
            try
            {
                symbol = CheckerAccess.GetSymbolAtLocation(checker, node, true);
            }
            catch (Exception exception)
            {
                RecordFailure(isSymbolWalk: true, node, exception);
                symbol = GetFallbackSymbol(checker, node);
            }
            if (symbol is null)
            {
                return null;
            }

            string symbolName;
            try
            {
                symbolName = CheckerAccess.SymbolToStringEx(
                    checker,
                    symbol,
                    node.Parent,
                    global::Ts.Ast.SymbolFlags.None,
                    global::Ts.Checker.SymbolFormatFlags.AllowAnyNodeKind);
            }
            catch (Exception exception)
            {
                RecordFailure(isSymbolWalk: true, node, exception);
                symbolName = global::Ts.Ast.AstUtilities.SymbolName(symbol);
            }

            var symbolString = new StringBuilder(256);
            symbolString.Append("Symbol(");
            symbolString.Append(global::Ts.Ast.AstUtilities.EscapeAllInternalSymbolNames(symbolName));
            global::Ts.Ast.Node[] declarations = symbol.Declarations ?? [];
            int count = 0;
            foreach (global::Ts.Ast.Node declaration in declarations)
            {
                if (count >= 5)
                {
                    symbolString.Append(" ... and ");
                    symbolString.Append((declarations.Length - count).ToString(CultureInfo.InvariantCulture));
                    symbolString.Append(" more");
                    break;
                }
                count++;
                symbolString.Append(", ");

                global::Ts.Ast.SourceFile? declarationSourceFile =
                    global::Ts.Ast.AstUtilities.GetSourceFileOfNode(declaration);
                if (declarationSourceFile is null)
                {
                    RecordFailure(
                        isSymbolWalk: true,
                        node,
                        new InvalidOperationException("symbol declaration has no source file"));
                    continue;
                }
                (int declarationLine, int declarationCharacter) =
                    global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(
                        declarationSourceFile,
                        declaration.Pos);
                string fileName = global::Ts.TsPath.TsPath.GetBaseFileName(
                    declarationSourceFile.FileName());
                symbolString.Append("Decl(");
                symbolString.Append(fileName);
                symbolString.Append(", ");
                if (IsDefaultLibraryFile(fileName))
                {
                    symbolString.Append("--, --)");
                }
                else
                {
                    symbolString.Append(declarationLine.ToString(CultureInfo.InvariantCulture));
                    symbolString.Append(", ");
                    symbolString.Append(declarationCharacter.ToString(CultureInfo.InvariantCulture));
                    symbolString.Append(')');
                }
            }
            symbolString.Append(')');
            return new TypeSymbolBaselineEntry(line, sourceText, symbolString.ToString());
        }

        private global::Ts.Checker.Type GetTypeAtLocation(
            global::Ts.Checker.Checker checker,
            global::Ts.Ast.Node node)
        {
            global::Ts.Ast.Node? parent = node.Parent;
            if (parent is null)
            {
                throw new InvalidOperationException("type query node has no parent");
            }

            // Workaround to ensure we output 'C' instead of 'typeof C' for base class expressions.
            if (IsExpressionWithTypeArgumentsInClassExtendsClause(parent))
            {
                throw new NotSupportedException(
                    "GetTypeAtLocation(class extends ExpressionWithTypeArguments) is not ported");
            }

            if (global::Ts.Ast.AstUtilities.IsExpressionNode(node))
            {
                global::Ts.Ast.Node expression = node;
                if (parent.Kind == global::Ts.Ast.SyntaxKind.PropertyAccessExpression &&
                    parent.Name() == node)
                {
                    expression = parent;
                }
                global::Ts.Checker.Type expressionType =
                    CheckerAccess.GetTypeOfExpression(checker, expression);
                return CheckerAccess.GetRegularTypeOfLiteralType(checker, expressionType);
            }

            if (IsDeclarationName(node))
            {
                global::Ts.Ast.Symbol? symbol = CheckerAccess.GetSymbolOfDeclaration(checker, parent);
                if (symbol is null)
                {
                    throw new InvalidOperationException("declaration has no symbol");
                }
                return global::Ts.Ast.AstUtilities.IsTypeDeclaration(parent)
                    ? CheckerAccess.GetDeclaredTypeOfSymbol(checker, symbol)
                    : CheckerAccess.GetTypeOfSymbol(checker, symbol);
            }

            throw new NotSupportedException(
                "GetTypeAtLocation compatibility path is missing for " + node.Kind.ToString());
        }

        private global::Ts.Ast.Symbol? GetFallbackSymbol(
            global::Ts.Checker.Checker checker,
            global::Ts.Ast.Node node)
        {
            global::Ts.Ast.Node? parent = node.Parent;
            if (parent is null)
            {
                return null;
            }
            try
            {
                if (IsDeclarationName(node))
                {
                    return CheckerAccess.GetSymbolOfDeclaration(checker, parent);
                }
                if (global::Ts.Ast.NodeTests.IsIdentifier(node))
                {
                    return CheckerAccess.GetResolvedSymbol(checker, node);
                }
            }
            catch (Exception exception)
            {
                RecordFailure(isSymbolWalk: true, node, exception);
            }
            return null;
        }

        private void RecordFailure(bool isSymbolWalk, global::Ts.Ast.Node? node, Exception exception)
        {
            if (isSymbolWalk)
            {
                SymbolQueryFailures++;
            }
            else
            {
                TypeQueryFailures++;
            }

            if (queryFailureExamples.Count >= MaximumFailureExamples)
            {
                return;
            }
            string location = node is null
                ? "<file>"
                : node.Kind.ToString() + "@" + node.Pos.ToString(CultureInfo.InvariantCulture);
            queryFailureExamples.Add(
                (isSymbolWalk ? "symbol " : "type ") + location + ": " +
                exception.GetType().Name + ": " + exception.Message.ReplaceLineEndings(" "));
        }
    }

    private static bool IsDeclarationName(global::Ts.Ast.Node node)
    {
        global::Ts.Ast.Node? parent = node.Parent;
        return !global::Ts.Ast.NodeTests.IsSourceFile(node) &&
            !global::Ts.Ast.AstUtilities.IsBindingPattern(node) &&
            parent is not null &&
            global::Ts.Ast.AstUtilities.IsDeclaration(parent) &&
            parent.Name() == node;
    }

    private static bool DeclarationHasValueMeaning(global::Ts.Ast.Node declaration) =>
        declaration.Kind switch
        {
            global::Ts.Ast.SyntaxKind.TypeParameter or
            global::Ts.Ast.SyntaxKind.InterfaceDeclaration or
            global::Ts.Ast.SyntaxKind.TypeAliasDeclaration or
            global::Ts.Ast.SyntaxKind.JSTypeAliasDeclaration or
            global::Ts.Ast.SyntaxKind.TypeLiteral => false,
            global::Ts.Ast.SyntaxKind.ModuleDeclaration =>
                global::Ts.Ast.AstUtilities.IsAmbientModule(declaration),
            _ => true,
        };

    private static bool IsTypeOrJsTypeAliasDeclaration(global::Ts.Ast.Node node) =>
        node.Kind == global::Ts.Ast.SyntaxKind.TypeAliasDeclaration ||
        node.Kind == global::Ts.Ast.SyntaxKind.JSTypeAliasDeclaration;

    private static bool IsExpressionWithTypeArgumentsInClassExtendsClause(global::Ts.Ast.Node node)
    {
        global::Ts.Ast.Node? heritageClause = node.Parent;
        global::Ts.Ast.Node? classLike = heritageClause?.Parent;
        return node.Kind == global::Ts.Ast.SyntaxKind.ExpressionWithTypeArguments &&
            heritageClause?.Kind == global::Ts.Ast.SyntaxKind.HeritageClause &&
            classLike is not null &&
            global::Ts.Ast.AstUtilities.IsClassLike(classLike) &&
            heritageClause.AsHeritageClause().Token == global::Ts.Ast.SyntaxKind.ExtendsKeyword;
    }

    private static bool IsLabelName(global::Ts.Ast.Node node)
    {
        global::Ts.Ast.Node? parent = node.Parent;
        return parent is not null && global::Ts.Ast.NodeTests.IsIdentifier(node) &&
            ((parent.Kind == global::Ts.Ast.SyntaxKind.LabeledStatement && parent.Label() == node) ||
                ((parent.Kind == global::Ts.Ast.SyntaxKind.BreakStatement ||
                    parent.Kind == global::Ts.Ast.SyntaxKind.ContinueStatement) &&
                    parent.Label() == node));
    }

    private static bool IsImportStatementName(global::Ts.Ast.Node node)
    {
        global::Ts.Ast.Node? parent = node.Parent;
        if (parent is null)
        {
            return false;
        }
        if (global::Ts.Ast.NodeTests.IsImportSpecifier(parent) &&
            (node == parent.Name() || node == parent.PropertyName()))
        {
            return true;
        }
        if (global::Ts.Ast.NodeTests.IsImportClause(parent) && node == parent.Name())
        {
            return true;
        }
        return global::Ts.Ast.NodeTests.IsImportEqualsDeclaration(parent) && node == parent.Name();
    }

    private static bool IsExportStatementName(global::Ts.Ast.Node node)
    {
        global::Ts.Ast.Node? parent = node.Parent;
        if (parent is null)
        {
            return false;
        }
        if (global::Ts.Ast.NodeTests.IsExportAssignment(parent) && node == parent.Expression())
        {
            return true;
        }
        return global::Ts.Ast.NodeTests.IsExportSpecifier(parent) &&
            (node == parent.Name() || node == parent.PropertyName());
    }

    private static bool IsIntrinsicJsxTag(
        global::Ts.Ast.Node node,
        global::Ts.Ast.SourceFile sourceFile)
    {
        global::Ts.Ast.Node? parent = node.Parent;
        if (parent is null ||
            !(global::Ts.Ast.NodeTests.IsJsxOpeningElement(parent) ||
                global::Ts.Ast.NodeTests.IsJsxClosingElement(parent) ||
                global::Ts.Ast.NodeTests.IsJsxSelfClosingElement(parent)))
        {
            return false;
        }
        if (parent.TagName() != node)
        {
            return false;
        }
        string text = global::Ts.Scanner.Scanner.GetSourceTextOfNodeFromSourceFile(
            sourceFile,
            node,
            includeTrivia: false);
        return global::Ts.Scanner.Scanner.IsIntrinsicJsxName(text);
    }

    private static class CheckerAccess
    {
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetTypeOfExpression")]
        public static extern global::Ts.Checker.Type GetTypeOfExpression(
            global::Ts.Checker.Checker checker,
            global::Ts.Ast.Node node);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetRegularTypeOfLiteralType")]
        public static extern global::Ts.Checker.Type GetRegularTypeOfLiteralType(
            global::Ts.Checker.Checker checker,
            global::Ts.Checker.Type type);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetSymbolOfDeclaration")]
        public static extern global::Ts.Ast.Symbol? GetSymbolOfDeclaration(
            global::Ts.Checker.Checker checker,
            global::Ts.Ast.Node node);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetTypeOfSymbol")]
        public static extern global::Ts.Checker.Type GetTypeOfSymbol(
            global::Ts.Checker.Checker checker,
            global::Ts.Ast.Symbol symbol);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetDeclaredTypeOfSymbol")]
        public static extern global::Ts.Checker.Type GetDeclaredTypeOfSymbol(
            global::Ts.Checker.Checker checker,
            global::Ts.Ast.Symbol symbol);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TypeToStringEx")]
        public static extern string TypeToStringEx(
            global::Ts.Checker.Checker checker,
            global::Ts.Checker.Type type,
            global::Ts.Ast.Node? enclosingDeclaration,
            global::Ts.Checker.TypeFormatFlags flags,
            object? verbosityContext);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetSymbolAtLocation")]
        public static extern global::Ts.Ast.Symbol? GetSymbolAtLocation(
            global::Ts.Checker.Checker checker,
            global::Ts.Ast.Node node,
            bool ignoreErrors);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetResolvedSymbol")]
        public static extern global::Ts.Ast.Symbol GetResolvedSymbol(
            global::Ts.Checker.Checker checker,
            global::Ts.Ast.Node node);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SymbolToStringEx")]
        public static extern string SymbolToStringEx(
            global::Ts.Checker.Checker checker,
            global::Ts.Ast.Symbol symbol,
            global::Ts.Ast.Node? enclosingDeclaration,
            global::Ts.Ast.SymbolFlags meaning,
            global::Ts.Checker.SymbolFormatFlags flags);
    }
}
