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
    string? Failure,
    ConformanceTypeSymbolResult? TypeSymbols = null);

public sealed record ConformanceArtifactResult(
    bool Attempted,
    bool Matched,
    bool ReferencePresent,
    int QueryFailures,
    string? Failure);

public sealed record ConformanceTypeSymbolResult(
    ConformanceArtifactResult Types,
    ConformanceArtifactResult Symbols);

public sealed record ConformanceRunSummary(IReadOnlyList<ConformanceCaseResult> Results)
{
    public int Total => Results.Count;
    public int Passed => Results.Count(result => result.Matched);
    public int Failed => Total - Passed;
    public int ReferencesPresent => Results.Count(result => result.ReferencePresent);
}

public static partial class CompilerBaselineRunner
{
    private static readonly object s_emitDebugLock = new();
    private const string SourceFolder = "/.src";
    private const string LibraryFolder = "/.lib";
    // The default bundled libs (lib.es5.d.ts, …) live under the `bundled:///libs` scheme, exactly as
    // the Go harness does (bundled.LibPath() → "bundled:///libs", embed.go). This is NOT the same as
    // LibraryFolder ("/.lib" = harnessutil.go:39 testLibFolder for the submodule tests/lib content).
    // The distinction is load-bearing for diagnostic ordering: CompareDiagnostics sorts by full
    // FileName, and "bundled:///libs/…" sorts AFTER "/.src/…" ('b' > '/') while "/.lib/…" wrongly
    // sorts before it ('l' < 's'). ErrorBaseline.RemoveTestPathPrefixes already strips this scheme.
    private const string BundledLibraryFolder = "bundled:///libs";

    public static bool TypesAndSymbolsEnabled => string.Equals(
        Environment.GetEnvironmentVariable("TSCS_CONFORMANCE_TYPES"),
        "1",
        StringComparison.Ordinal);

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

    // Go harnessutil.HarnessOptions field set (harnessutil.go:62-77): these directives are routed
    // to harness options by SetOptionsFromTestConfig and must never reach the compiler option
    // parser (an unknown-option diagnostic would corrupt the errors baseline). Options that only
    // affect types/js baselines (noTypesAndSymbols, fullEmitPaths, ...) are ignorable for the
    // errors.txt wave; libFiles is honored below (harnessutil.go:145-154); the remaining
    // behavior-affecting ones (useCaseSensitiveFileNames, ...) simply stay non-matching and land
    // in failingCases.txt until honored.
    private static readonly HashSet<string> s_harnessOnlyOptions = new(StringComparer.Ordinal)
    {
        "baselinefile",
        "capturesuggestions",
        "currentdirectory",
        "filename",
        "fullemitpaths",
        "includebuiltfile",
        "libfiles",
        "link",
        "noimplicitreferences",
        "notypesandsymbols",
        "reportdiagnostics",
        "symlink",
        "typescriptversion",
        "usecasesensitivefilenames",
    };

    // harnessutil.go:293-311 — compiler options the harness recognizes on top of OptionsDeclarations.
    // tsgo's SetOptionsFromTestConfig applies test-config options via getCommandLineOption +
    // ParseCompilerOptions (never through CLI-flag string parsing), so these — which are NOT
    // command-line flags in OptionsDeclarations — are accepted. Our runner otherwise routes every
    // directive through ParseCommandLine, which would reject `--suppressoutputpathcheck` with TS5023
    // (and then treat the value `true` as a root file → TS6231). Map the lowercase test-directive key
    // to the canonical option name and apply it directly onto the parsed CompilerOptions instead.
    private static readonly Dictionary<string, string> s_harnessExtraCompilerOptions = new(StringComparer.Ordinal)
    {
        ["allownontsextensions"] = "allowNonTsExtensions",
        ["noerrortruncation"] = "noErrorTruncation",
        ["suppressoutputpathcheck"] = "suppressOutputPathCheck",
        ["nocheck"] = "noCheck",
    };

    // The default bundled libs are served by Bundled.WrapFS (the `bundled:///libs` overlay, ported
    // from bundled/embed.go) rather than mounted into the test VFS map — matching the Go harness
    // (harnessutil.go:212 bundled.WrapFS(fs)). This keeps their FileName under `bundled:///libs/…`,
    // which is what CompareDiagnostics sorts by.

    // Go harnessutil.go:241-264 testLibFolderMap (sync.OnceValue): every file under the
    // TypeScript submodule's tests/lib directory, keyed by its `/.lib/<relative path>` VFS path.
    // Only merged into a test VFS when the case actually references the folder (includeLibDir).
    private static readonly Lazy<IReadOnlyDictionary<string, string>> s_testLibFolderContents = new(
        () =>
        {
            string libRoot = Path.Combine(RepoPaths.TypeScriptSubmodulePath, "tests", "lib");
            var contents = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string filePath in Directory.EnumerateFiles(libRoot, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(libRoot, filePath).Replace('\\', '/');
                contents[LibraryFolder + "/" + relative] = File.ReadAllText(filePath, Encoding.UTF8);
            }
            return contents;
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

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
                Failure: DescribeException(exception));
        }
    }

    // Failure text used for root-cause clustering: exception type + message + the first Ts.* stack
    // frame (NotImplementedException stub messages are otherwise indistinguishable). The frame is
    // located by substring so localized stack-trace prefixes ("at"/"在") do not matter.
    public static string DescribeException(Exception exception)
    {
        string head = exception.GetType().Name + ": " + exception.Message.ReplaceLineEndings(" ");
        string? trace = exception.StackTrace;
        if (trace is not null)
        {
            foreach (string line in trace.Split('\n'))
            {
                int start = line.IndexOf("Ts.", StringComparison.Ordinal);
                if (start < 0)
                {
                    continue;
                }
                int paren = line.IndexOf('(', start);
                string frame = paren > start ? line[start..paren] : line[start..].TrimEnd();
                return head + " @ " + frame;
            }
        }
        return head;
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

        var rootPaths = SelectRootPaths(unitsByPath, parsed.GlobalOptions);

        // Performance optimization; avoid copying in the /.lib folder if the test doesn't need it.
        // Go harnessutil.go:142 scans the compiled input files (toBeCompiled) only, not otherFiles;
        // rootPaths is exactly that set here.
        bool includeLibDir = unitsByPath.Any(entry =>
            rootPaths.Contains(entry.Path, StringComparer.Ordinal) &&
            entry.Unit.Content.Contains(LibraryFolder + "/", StringComparison.Ordinal));

        // Files from tests/lib that are requested by "@libFiles" join the program file set after
        // the test units (Go harnessutil.go:145-154); they are compiled but never baselined.
        var programRoots = rootPaths;
        if (parsed.GlobalOptions.TryGetValue("libfiles", out string? libFilesValue))
        {
            bool noLib = parsed.GlobalOptions.TryGetValue("nolib", out string? noLibValue) &&
                string.Equals(noLibValue, "true", StringComparison.OrdinalIgnoreCase);
            foreach (string libFile in ParseLibFilesList(libFilesValue))
            {
                if (string.Equals(libFile, "lib.d.ts", StringComparison.Ordinal) && !noLib)
                {
                    // Go harnessutil.go:148: "We used to override lib with a custom lib.d.ts for
                    // some reason. Skip this unless it becomes necessary."
                    continue;
                }
                if (ReferenceEquals(programRoots, rootPaths))
                {
                    programRoots = new List<string>(rootPaths);
                }
                programRoots.Add(global::Ts.TsPath.TsPath.CombinePaths(LibraryFolder, libFile));
                includeLibDir = true;
            }
        }

        // map-iter: 已排序 — symlink creation order is deterministic and has no output effect.
        foreach (string link in parsed.Symlinks.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            string linkPath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(link, currentDirectory);
            string targetPath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(parsed.Symlinks[link], currentDirectory);
            vfsEntries[linkPath] = VfsTestUtil.Symlink(targetPath);
        }

        // Go harnessutil.go:207-209: maps.Copy(testfs, testLibFolderMap()) — mounted last, after
        // test units and symlinks, so a tests/lib file wins over any same-path entry.
        if (includeLibDir)
        {
            // map-iter: 序无关 — dictionary insertion into the VFS map; VFS lookup is by path.
            foreach ((string libPath, string libContent) in s_testLibFolderContents.Value)
            {
                vfsEntries[libPath] = libContent;
            }
        }

        // harnessutil.go:212 — wrap with the bundled overlay so `bundled:///libs/*` resolves to the
        // embedded default libs (kept out of the rooted-disk-path test VFS map, exactly as Go does).
        IFileSystem fs = global::Ts.Bundled.Bundled.WrapFS(
            VfsTestUtil.FromMap(vfsEntries, useCaseSensitiveFileNames: true));
        var configHost = new VfsParseConfigHost(fs, currentDirectory);
        ParsedCommandLine config = ParseCompilerOptions(configHost, parsed.GlobalOptions, programRoots);
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
            BundledLibraryFolder,
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
            // harnessutil.go:639-641 — declaration diagnostics are part of both the pre- and post-emit sets.
            if (program.Options()!.GetEmitDeclarations())
            {
                diagnostics.AddRange(program.GetDeclarationDiagnostics(CancellationToken.None, sourceFile: null));
            }
        }
        catch (Exception exception)
        {
            // The checker is being ported concurrently. Preserve and baseline every completed
            // compiler stage instead of losing syntactic diagnostics when a later checker stub
            // throws; the result remains non-matching and is Skip-gated with this precise reason.
            checkerFailure = DescribeException(exception);
        }
        Diagnostic[] sortedDiagnostics = Program.SortAndDeduplicateDiagnostics(diagnostics.ToArray());

        // harnessutil.go:626-693 — pre/post-emit double-run. Run Emit on a fresh program and, if the
        // post-emit diagnostic count differs from pre-emit, synthesize the TS-1 "counts do not match"
        // ad-hoc diagnostic listing the excess (a semantic error the emit resolver adds but which the
        // command line/editor never surface). Skipped only when the pre-emit checker itself failed.
        if (checkerFailure is null)
        {
            try
            {
                var postProgram = new Program(new ProgramOptions { Host = host, Config = config, SingleThreaded = singleThreaded ? Tristate.True : Tristate.Unknown });
                var postErrors = new List<Diagnostic>();
                postErrors.AddRange(postProgram.GetConfigFileParsingDiagnostics());
                postErrors.AddRange(postProgram.GetProgramDiagnostics());
                postErrors.AddRange(postProgram.GetSyntacticDiagnostics(CancellationToken.None, sourceFile: null));
                postProgram.Emit(CancellationToken.None, new EmitOptions { WriteFile = static (_, _, _) => { } });
                postErrors.AddRange(postProgram.GetSemanticDiagnostics(CancellationToken.None, sourceFile: null));
                postErrors.AddRange(postProgram.GetGlobalDiagnostics(CancellationToken.None));
                // harnessutil.go:655-657
                if (postProgram.Options()!.GetEmitDeclarations())
                {
                    postErrors.AddRange(postProgram.GetDeclarationDiagnostics(CancellationToken.None, sourceFile: null));
                }
                Diagnostic[] post = Program.SortAndDeduplicateDiagnostics(postErrors.ToArray());
                if (post.Length != sortedDiagnostics.Length)
                {
                    Diagnostic[] longer = post.Length > sortedDiagnostics.Length ? post : sortedDiagnostics;
                    Diagnostic[] shorter = post.Length > sortedDiagnostics.Length ? sortedDiagnostics : post;
                    var diag = Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.NewAdHocMessage(
                        $"Pre-emit ({sortedDiagnostics.Length}) and post-emit ({post.Length}) diagnostic counts do not match! This can indicate that a semantic _error_ was added by the emit resolver - such an error may not be reflected on the command line or in the editor, but may be captured in a baseline here!"));
                    diag = diag.AddRelatedInfo(Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.NewAdHocMessage("The excess diagnostics are:")));
                    foreach (var d in longer)
                    {
                        if (!shorter.Any(s => global::Ts.Ast.DiagnosticsCollection.CompareDiagnostics(d, s) == 0))
                        {
                            diag = diag.AddRelatedInfo(d);
                        }
                    }
                    sortedDiagnostics = Program.SortAndDeduplicateDiagnostics([.. shorter, diag]);
                }
                else
                {
                    // harnessutil.go:693 — when counts match, tsgo returns postErrors. (Historical note:
                    // before classfields.go was ported, the emit-time const-enum inliner queried
                    // GetConstantValue on untransformed x.#x nodes and double-reported TS18014, reordering
                    // related infos in post; with the transformer live, pre and post are byte-identical
                    // except where the emit resolver legitimately adds/moves diagnostics.)
                    sortedDiagnostics = post;
                }
            }
            catch (Exception)
            {
                // The emit pipeline is being ported incrementally; a case whose Emit cannot complete
                // (e.g. an unported transformer path) simply skips the double-run and keeps its pre-emit
                // diagnostics — matching how it behaved before the harness was wired.
            }
        }

        var baselineFiles = OrderBaselineFiles(unitsByPath, rootPaths);
        var context = new BaselineTestContext();
        ErrorBaseline.Run(
            context,
            testCase.BaselineName,
            baselineFiles,
            sortedDiagnostics,
            new Baseline.Options { Subfolder = Path.Combine("submodule", "conformance") });

        bool errorsMatched = !context.Failed && checkerFailure is null;
        ConformanceTypeSymbolResult? typeSymbols = null;
        if (TypesAndSymbolsEnabled &&
            errorsMatched &&
            !NoTypesAndSymbols(parsed.GlobalOptions))
        {
            typeSymbols = RunTypeAndSymbolBaselines(
                testCase,
                program,
                baselineFiles,
                hasErrorBaseline: sortedDiagnostics.Length != 0);
        }

        return new ConformanceCaseResult(
            name,
            Matched: errorsMatched,
            referencePresent,
            sortedDiagnostics.Length,
            JoinFailures(checkerFailure, context.Failed ? string.Join(" | ", context.Errors) : null),
            typeSymbols);
    }

    private static ConformanceTypeSymbolResult RunTypeAndSymbolBaselines(
        CuratedConformanceCase testCase,
        Program program,
        IReadOnlyList<BaselineTestFile> baselineFiles,
        bool hasErrorBaseline)
    {
        string baselinePath = Path.GetFileName(testCase.RelativePath);
        string header = "tests/cases/conformance/" + testCase.RelativePath.Replace('\\', '/');
        string baseName = GetCaseName(testCase.BaselineName);
        string referenceFolder = Path.Combine(Baseline.ReferenceRoot, "submodule", "conformance");
        string typeReference = Path.Combine(referenceFolder, baseName + ".types");
        string symbolReference = Path.Combine(referenceFolder, baseName + ".symbols");
        bool typeReferencePresent = File.Exists(typeReference);
        bool symbolReferencePresent = File.Exists(symbolReference);
        var typeContext = new BaselineTestContext();
        var symbolContext = new BaselineTestContext();

        try
        {
            TypeSymbolBaselineGeneration generation = TypeSymbolBaseline.Run(
                typeContext,
                symbolContext,
                baselinePath,
                header,
                program,
                baselineFiles,
                new Baseline.Options { Subfolder = Path.Combine("submodule", "conformance") },
                hasErrorBaseline);

            string? typeFailure = ArtifactFailure(typeContext, generation.TypeQueryFailures, generation.QueryFailureExamples, "type");
            string? symbolFailure = ArtifactFailure(symbolContext, generation.SymbolQueryFailures, generation.QueryFailureExamples, "symbol");
            return new ConformanceTypeSymbolResult(
                new ConformanceArtifactResult(
                    Attempted: true,
                    Matched: !typeContext.Failed && generation.TypeQueryFailures == 0,
                    typeReferencePresent,
                    generation.TypeQueryFailures,
                    typeFailure),
                new ConformanceArtifactResult(
                    Attempted: true,
                    Matched: !symbolContext.Failed && generation.SymbolQueryFailures == 0,
                    symbolReferencePresent,
                    generation.SymbolQueryFailures,
                    symbolFailure));
        }
        catch (Exception exception)
        {
            string failure = DescribeException(exception);
            return new ConformanceTypeSymbolResult(
                new ConformanceArtifactResult(
                    Attempted: true,
                    Matched: false,
                    typeReferencePresent,
                    QueryFailures: 1,
                    failure),
                new ConformanceArtifactResult(
                    Attempted: true,
                    Matched: false,
                    symbolReferencePresent,
                    QueryFailures: 1,
                    failure));
        }
    }

    private static string? ArtifactFailure(
        BaselineTestContext context,
        int queryFailures,
        IReadOnlyList<string> queryFailureExamples,
        string kind)
    {
        string? baselineFailure = context.Failed ? string.Join(" | ", context.Errors) : null;
        if (queryFailures == 0)
        {
            return baselineFailure;
        }
        string queryFailure = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{queryFailures} {kind} query fallback(s): {string.Join("; ", queryFailureExamples.Where(example => example.StartsWith(kind + " ", StringComparison.Ordinal)))}");
        return JoinFailures(queryFailure, baselineFailure);
    }

    private static bool NoTypesAndSymbols(IReadOnlyDictionary<string, string> globalOptions)
    {
        if (!globalOptions.TryGetValue("notypesandsymbols", out string? value))
        {
            return false;
        }
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedCommandLine ParseCompilerOptions(
        ParseConfigHost host,
        IReadOnlyDictionary<string, string> globalOptions,
        IReadOnlyList<string> roots)
    {
        var args = new List<string>();
        List<(string name, string value)>? extraCompilerOptions = null;
        // map-iter: 已排序 — compiler options are emitted in ordinal key order before parsing.
        foreach (string key in globalOptions.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (s_harnessOnlyOptions.Contains(key))
            {
                continue;
            }
            if (s_harnessExtraCompilerOptions.TryGetValue(key, out var canonical))
            {
                // Applied directly onto the parsed options below, matching tsgo's harness.
                (extraCompilerOptions ??= new()).Add((canonical, globalOptions[key]));
                continue;
            }
            args.Add("--" + key);
            if (globalOptions[key].Length != 0)
            {
                args.Add(globalOptions[key]);
            }
        }
        args.AddRange(roots);
        var config = global::Ts.TsOptions.CommandLineParser.ParseCommandLine(args.ToArray(), host);
        if (extraCompilerOptions != null)
        {
            var opts = config.CompilerOptions();
            if (opts != null)
            {
                foreach (var (name, value) in extraCompilerOptions)
                {
                    // All four harness-extra options are boolean; convert the string directive value
                    // to a bool so ParseTristate (which only recognizes the bool true) sets True.
                    object boolValue = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                    global::Ts.TsOptions.ParsingHelpers.ParseCompilerOptions(name, boolValue, opts);
                }
            }
        }
        return config;
    }

    // Go tsoptions.ParseListTypeOption (commandlineparser.go:339-366) as applied to the string-
    // element "libFiles" harness option: trim the whole value; a "-" prefix or empty value yields
    // no entries; otherwise split on ',' and drop empty elements (elements are not trimmed).
    private static List<string> ParseLibFilesList(string value)
    {
        value = value.Trim();
        var result = new List<string>();
        if (value.StartsWith('-') || value.Length == 0)
        {
            return result;
        }
        foreach (string element in value.Split(','))
        {
            if (element.Length != 0)
            {
                result.Add(element);
            }
        }
        return result;
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
