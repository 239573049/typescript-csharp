// Curated first slice of tsgo internal/testrunner/compiler_runner.go.
//
// Each case is parsed by Ts.TestUtil.TestCaseParser, materialized in the same `/.src` VFS
// namespace as the Go harness, compiled through the public CompilerHost + Program pipeline, and
// compared byte-for-byte with testdata/baselines/reference/submodule/conformance/*.errors.txt.
// The full 5,907-case matrix, vary-by expansion, tsconfig projects, emit baselines, and bundled FS
// product overlay remain later waves; this runner deliberately selects single-configuration cases
// whose primary currency is basic type diagnostics.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Ts.Ast;
using Ts.Compiler;
using Ts.Core;
using Ts.TestUtil;
using Ts.TsOptions;
using Ts.Vfs;
using Ts.Vfs.VfsTest;

namespace Ts.Conformance.Tests;

public sealed record CuratedConformanceCase(string RelativePath, string BaselineName);

public sealed record ConformanceCaseResult(
    string Name,
    bool Matched,
    bool ReferencePresent,
    int DiagnosticCount,
    string? Failure);

public sealed record ConformanceRunSummary(IReadOnlyList<ConformanceCaseResult> Results)
{
    public int Total => Results.Count;
    public int Passed => Results.Count(result => result.Matched);
    public int Failed => Total - Passed;
    public int ReferencesPresent => Results.Count(result => result.ReferencePresent);
}

public static partial class CompilerBaselineRunner
{
    private const string SourceFolder = "/.src";
    private const string LibraryFolder = "/.lib";

    public static readonly CuratedConformanceCase[] CuratedCases =
    [
        new("types/primitives/boolean/assignFromBooleanInterface.ts", "assignFromBooleanInterface.errors.txt"),
        new("types/primitives/number/assignFromNumberInterface.ts", "assignFromNumberInterface.errors.txt"),
        new("types/primitives/string/assignFromStringInterface.ts", "assignFromStringInterface.errors.txt"),
        new("expressions/commaOperator/commaOperatorInvalidAssignmentType.ts", "commaOperatorInvalidAssignmentType.errors.txt"),
        new("types/primitives/void/invalidAssignmentsToVoid.ts", "invalidAssignmentsToVoid.errors.txt"),
        new("types/primitives/void/invalidVoidAssignments.ts", "invalidVoidAssignments.errors.txt"),
        new("types/primitives/boolean/boolInsteadOfBoolean.ts", "boolInsteadOfBoolean.errors.txt"),
        new("types/nonPrimitive/nonPrimitiveAssignError.ts", "nonPrimitiveAssignError.errors.txt"),
        new("expressions/valuesAndReferences/assignmentToParenthesizedIdentifiers.ts", "assignmentToParenthesizedIdentifiers.errors.txt"),
        new("types/typeRelationships/assignmentCompatibility/assignmentCompatWithObjectMembersOptionality.ts", "assignmentCompatWithObjectMembersOptionality.errors.txt"),
        new("types/typeRelationships/assignmentCompatibility/typeParameterAssignability.ts", "typeParameterAssignability.errors.txt"),
        new("types/objectTypeLiteral/callSignatures/callSignatureWithOptionalParameterAndInitializer.ts", "callSignatureWithOptionalParameterAndInitializer.errors.txt"),
        new("parser/ecmascript5/ErrorRecovery/ArgumentLists/parserErrorRecovery_ArgumentList1.ts", "parserErrorRecovery_ArgumentList1.errors.txt"),
        new("es7/trailingCommasInBindingPatterns.ts", "trailingCommasInBindingPatterns.errors.txt"),
    ];

    private static readonly HashSet<string> s_harnessOnlyOptions = new(StringComparer.Ordinal)
    {
        "currentdirectory",
        "filename",
        "link",
        "noimplicitreferences",
        "symlink",
    };

    public static ConformanceRunSummary RunCurated()
    {
        var results = new List<ConformanceCaseResult>(CuratedCases.Length);
        foreach (CuratedConformanceCase testCase in CuratedCases)
        {
            results.Add(RunOne(testCase));
        }
        return new ConformanceRunSummary(results);
    }

    public static ConformanceCaseResult RunOne(CuratedConformanceCase testCase)
    {
        string name = GetCaseName(testCase.BaselineName);
        string referencePath = Path.Combine(
            Baseline.ReferenceRoot,
            "submodule",
            "conformance",
            testCase.BaselineName);
        bool referencePresent = File.Exists(referencePath);

        try
        {
            return LargeStackExecutor.Run(() => RunOneCore(testCase, name, referencePresent));
        }
        catch (Exception exception)
        {
            return new ConformanceCaseResult(
                name,
                Matched: false,
                referencePresent,
                DiagnosticCount: 0,
                Failure: exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static ConformanceCaseResult RunOneCore(
        CuratedConformanceCase testCase,
        string name,
        bool referencePresent)
    {
        string casePath = Path.Combine(
            RepoPaths.TypeScriptSubmodulePath,
            "tests",
            "cases",
            "conformance",
            testCase.RelativePath);
        string content = File.ReadAllText(casePath, Encoding.UTF8);
        ParseTestFilesResult<TestUnit> parsed = TestCaseParser.ParseTestFilesAndSymlinks(
            content,
            casePath,
            static (fileName, fileContent, _) => new TestUnit(fileName, fileContent));
        if (parsed.Error is not null)
        {
            throw new InvalidOperationException("failed to parse conformance test units", parsed.Error);
        }

        string currentDirectory = parsed.CurrentDirectory.Length == 0
            ? SourceFolder
            : global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(parsed.CurrentDirectory, SourceFolder);

        var vfsEntries = new Dictionary<string, object>(StringComparer.Ordinal);
        var unitsByPath = new List<(TestUnit Unit, string Path)>(parsed.Units.Count);
        foreach (TestUnit unit in parsed.Units)
        {
            string path = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(unit.Name, currentDirectory);
            unitsByPath.Add((unit, path));
            vfsEntries[path] = unit.Content;
        }

        // The product-side bundled.WrapFS equivalent is not exported yet. Load the generated lib
        // copies into this test VFS so Program sees the same `/.lib` namespace as the Go harness.
        string bundledLibRoot = Path.Combine(RepoPaths.RootPath, "src", "Ts.Compiler", "Bundled", "libs");
        foreach (string libName in global::Ts.Bundled.Bundled.LibNames)
        {
            string diskPath = Path.Combine(bundledLibRoot, libName);
            vfsEntries[global::Ts.TsPath.TsPath.CombinePaths(LibraryFolder, libName)] =
                File.ReadAllText(diskPath, Encoding.UTF8);
        }

        // map-iter: 已排序 — symlink creation order is deterministic and has no output effect.
        foreach (string link in parsed.Symlinks.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            string linkPath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(link, currentDirectory);
            string targetPath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(parsed.Symlinks[link], currentDirectory);
            vfsEntries[linkPath] = VfsTestUtil.Symlink(targetPath);
        }

        IFileSystem fs = VfsTestUtil.FromMap(vfsEntries, useCaseSensitiveFileNames: true);
        var rootPaths = SelectRootPaths(unitsByPath, parsed.GlobalOptions);
        var configHost = new VfsParseConfigHost(fs, currentDirectory);
        ParsedCommandLine config = ParseCompilerOptions(configHost, parsed.GlobalOptions, rootPaths);
        CompilerOptions options = config.CompilerOptions()!;
        if (options.NewLine == NewLineKind.None)
        {
            options.NewLine = NewLineKind.CRLF;
        }
        if (options.SkipDefaultLibCheck.IsUnknown())
        {
            options.SkipDefaultLibCheck = Tristate.True;
        }
        options.NoErrorTruncation = Tristate.True;
        options.Types ??= [];

        CompilerHost host = CompilerHosts.NewCompilerHost(
            currentDirectory,
            fs,
            LibraryFolder,
            extendedConfigCache: null,
            trace: null);
        bool singleThreaded = string.Equals(
            Environment.GetEnvironmentVariable("TSCS_SINGLE_THREADED"),
            "1",
            StringComparison.Ordinal);
        var program = new Program(new ProgramOptions
        {
            Host = host,
            Config = config,
            SingleThreaded = singleThreaded ? Tristate.True : Tristate.Unknown,
        });

        var diagnostics = new List<Diagnostic>();
        diagnostics.AddRange(program.GetConfigFileParsingDiagnostics());
        diagnostics.AddRange(program.GetProgramDiagnostics());
        diagnostics.AddRange(program.GetSyntacticDiagnostics(CancellationToken.None, sourceFile: null));
        string? checkerFailure = null;
        try
        {
            diagnostics.AddRange(program.GetSemanticDiagnostics(CancellationToken.None, sourceFile: null));
            diagnostics.AddRange(program.GetGlobalDiagnostics(CancellationToken.None));
        }
        catch (Exception exception)
        {
            // The checker is being ported concurrently. Preserve and baseline every completed
            // compiler stage instead of losing syntactic diagnostics when a later checker stub
            // throws; the result remains non-matching and is Skip-gated with this precise reason.
            checkerFailure = exception.GetType().Name + ": " + exception.Message;
        }
        Diagnostic[] sortedDiagnostics = Program.SortAndDeduplicateDiagnostics(diagnostics.ToArray());

        var baselineFiles = OrderBaselineFiles(unitsByPath, rootPaths);
        var context = new BaselineTestContext();
        ErrorBaseline.Run(
            context,
            testCase.BaselineName,
            baselineFiles,
            sortedDiagnostics,
            new Baseline.Options { Subfolder = Path.Combine("submodule", "conformance") });

        return new ConformanceCaseResult(
            name,
            Matched: !context.Failed && checkerFailure is null,
            referencePresent,
            sortedDiagnostics.Length,
            JoinFailures(checkerFailure, context.Failed ? string.Join(" | ", context.Errors) : null));
    }

    private static ParsedCommandLine ParseCompilerOptions(
        ParseConfigHost host,
        IReadOnlyDictionary<string, string> globalOptions,
        IReadOnlyList<string> roots)
    {
        var args = new List<string>();
        // map-iter: 已排序 — compiler options are emitted in ordinal key order before parsing.
        foreach (string key in globalOptions.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (s_harnessOnlyOptions.Contains(key))
            {
                continue;
            }
            args.Add("--" + key);
            if (globalOptions[key].Length != 0)
            {
                args.Add(globalOptions[key]);
            }
        }
        args.AddRange(roots);
        return global::Ts.TsOptions.CommandLineParser.ParseCommandLine(args.ToArray(), host);
    }

    private static List<string> SelectRootPaths(
        IReadOnlyList<(TestUnit Unit, string Path)> units,
        IReadOnlyDictionary<string, string> globalOptions)
    {
        if (units.Count == 0)
        {
            return [];
        }

        TestUnit lastUnit = units[^1].Unit;
        bool noImplicitReferences = globalOptions.TryGetValue("noimplicitreferences", out string? value) && value.Length != 0;
        bool referencedFromLast = lastUnit.Content.Contains("require(", StringComparison.Ordinal) ||
            ReferencesRegex().IsMatch(lastUnit.Content);
        if (noImplicitReferences || referencedFromLast)
        {
            return [units[^1].Path];
        }
        return units.Select(unit => unit.Path).ToList();
    }

    private static List<BaselineTestFile> OrderBaselineFiles(
        IReadOnlyList<(TestUnit Unit, string Path)> units,
        IReadOnlyList<string> roots)
    {
        var result = new List<BaselineTestFile>(units.Count);
        foreach (string root in roots)
        {
            (TestUnit Unit, string Path) match = units.First(unit =>
                string.Equals(unit.Path, root, StringComparison.Ordinal));
            result.Add(new BaselineTestFile(match.Path, match.Unit.Content));
        }
        foreach ((TestUnit unit, string path) in units)
        {
            if (!roots.Contains(path, StringComparer.Ordinal))
            {
                result.Add(new BaselineTestFile(path, unit.Content));
            }
        }
        return result;
    }

    private sealed record TestUnit(string Name, string Content);

    public static string GetCaseName(string baselineName)
    {
        const string suffix = ".errors.txt";
        return baselineName.EndsWith(suffix, StringComparison.Ordinal)
            ? baselineName[..^suffix.Length]
            : Path.GetFileNameWithoutExtension(baselineName);
    }

    private static string? JoinFailures(string? checkerFailure, string? baselineFailure)
    {
        if (checkerFailure is null)
        {
            return baselineFailure;
        }
        return baselineFailure is null
            ? checkerFailure
            : checkerFailure + " | " + baselineFailure;
    }

    private sealed class VfsParseConfigHost : ParseConfigHost
    {
        private readonly IFileSystem fs;
        private readonly string currentDirectory;

        public VfsParseConfigHost(IFileSystem fs, string currentDirectory)
        {
            this.fs = fs;
            this.currentDirectory = currentDirectory;
        }

        public IFileSystem FS() => fs;

        public string GetCurrentDirectory() => currentDirectory;
    }

    [GeneratedRegex("reference\\s+path")]
    private static partial Regex ReferencesRegex();
}
