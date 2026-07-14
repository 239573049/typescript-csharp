// Full eligible corpus over _submodules/TypeScript/tests/cases/conformance.
//
// Enumeration follows tsgo internal/testrunner/compiler_runner.go: recursive walk of the suite
// directory matching `\.tsx?$` (compiler_runner.go:28, EnumerateTestFiles). The errors-baseline
// corpus narrows to cases whose full vary-by/emit machinery is not required yet:
//
//   1. basename must be unique across the suite (Go asserts this in compiler_runner_test.go:38-45;
//      a duplicate would make two cases claim the same baseline file);
//   2. the *plain* `<basename>.errors.txt` baseline must exist under
//      testdata/baselines/reference/submodule/conformance. Because tsgo derives configured
//      baseline names `name(k=v).errors.txt` whenever a vary-by matrix is present
//      (compiler_runner.go:271-277) and writes no errors baseline for error-free runs
//      (baseline.NoContent), plain-name existence simultaneously proves: single configuration,
//      at least one expected diagnostic, and that tsgo actually ran the case (unsupported-option
//      and skippedTests cases never produced a baseline);
//   3. the test must parse to exactly one unit with no symlinks (multi-file semantics — tsconfig
//      units, @link graphs, module resolution — are a later wave).
//
// Every rejected file is recorded with a reason so the selection arithmetic is auditable.
// Results are gated by failingCases.txt (see FailingCases): listed cases Skip on mismatch, all
// other cases must byte-match — the corpus is a regression ratchet while the checker is WIP.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Ts.TestUtil;

namespace Ts.Conformance.Tests;

public sealed record CorpusSkip(string RelativePath, string Reason);

public sealed record CorpusDirectoryRate(string Directory, int Matched, int Total);

public sealed record CorpusSelection(
    IReadOnlyList<CuratedConformanceCase> Selected,
    IReadOnlyList<CorpusSkip> Skipped,
    int TotalEnumerated)
{
    // map-iter: 已排序 — reasons are grouped then ordered by descending count, name-ordinal tiebreak.
    public IReadOnlyList<(string Reason, int Count)> SkipReasonCounts { get; } =
        Skipped
            .GroupBy(skip => skip.Reason, StringComparer.Ordinal)
            .Select(group => (Reason: group.Key, Count: group.Count()))
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Reason, StringComparer.Ordinal)
            .ToList();
}

public static class CorpusRunner
{
    public const string DebugWaveCapSkipReason = "beyond-debug-wave-cap";
    public const string MissingBaselineSkipReason = "missing-errors-baseline";
    public const string MultiFileSkipReason = "multi-file";
    public const string UnsupportedOptionSkipReason = "unsupported-option";

    public static bool IsFailingListUpdateRequested() => string.Equals(
        Environment.GetEnvironmentVariable("TSCS_CONFORMANCE_UPDATE_FAILING"),
        "1",
        StringComparison.Ordinal);

    public static bool UpdateFailingTypes => string.Equals(
        Environment.GetEnvironmentVariable("TSCS_CONFORMANCE_UPDATE_TYPES"),
        "1",
        StringComparison.Ordinal);

    private static readonly Lazy<CorpusSelection> s_selection = new(
        EnumerateCorpus,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static CorpusSelection Selection => s_selection.Value;

    public static CorpusSelection EnumerateAllEligible() => EnumerateCorpus(applyDebugWaveCap: false);

    private static CorpusSelection EnumerateCorpus() => EnumerateCorpus(applyDebugWaveCap: true);

    private static CorpusSelection EnumerateCorpus(bool applyDebugWaveCap)
    {
        // Evaluate the local-debug override only when enumeration actually runs. In particular,
        // do not capture it during type initialization or xUnit discovery.
        int? debugWaveCap = null;
        if (applyDebugWaveCap &&
            int.TryParse(
                Environment.GetEnvironmentVariable("TSCS_CONFORMANCE_WAVE_CAP"),
                out int parsedWaveCap) &&
            parsedWaveCap > 0)
        {
            debugWaveCap = parsedWaveCap;
        }

        string suiteRoot = Path.Combine(RepoPaths.TypeScriptSubmodulePath, "tests", "cases", "conformance");
        string referenceRoot = Path.Combine(Baseline.ReferenceRoot, "submodule", "conformance");
        var referenceNames = new HashSet<string>(
            Directory.EnumerateFiles(referenceRoot, "*.errors.txt").Select(Path.GetFileName)!,
            StringComparer.Ordinal);

        // Go compilerBaselineRegex `\.tsx?$` — .d.ts files match too, exactly like the Go walker.
        List<string> files = Directory
            .EnumerateFiles(suiteRoot, "*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".ts", StringComparison.Ordinal) ||
                file.EndsWith(".tsx", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(suiteRoot, file).Replace('\\', '/'))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();

        var basenameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string file in files)
        {
            string basename = GetBaseName(file);
            basenameCounts[basename] = basenameCounts.GetValueOrDefault(basename) + 1;
        }

        var eligible = new List<CuratedConformanceCase>();
        var skipped = new List<CorpusSkip>();
        foreach (string relativePath in files)
        {
            string basename = GetBaseName(relativePath);
            if (basenameCounts[basename] > 1)
            {
                skipped.Add(new CorpusSkip(relativePath, "duplicate-basename"));
                continue;
            }

            string casePath = Path.Combine(suiteRoot, relativePath);
            string content;
            try
            {
                content = File.ReadAllText(casePath, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                skipped.Add(new CorpusSkip(relativePath, "unreadable: " + exception.GetType().Name));
                continue;
            }

            ParseTestFilesResult<string> parsed = TestCaseParser.ParseTestFilesAndSymlinks(
                content,
                casePath,
                static (fileName, _, _) => fileName);
            if (parsed.Error is not null)
            {
                skipped.Add(new CorpusSkip(relativePath, "test-case-parse-error"));
                continue;
            }
            if (HasUnsupportedCompilerOptions(parsed.GlobalOptions))
            {
                skipped.Add(new CorpusSkip(relativePath, UnsupportedOptionSkipReason));
                continue;
            }

            string baselineName = TrimTsExtension(basename) + ".errors.txt";
            if (!referenceNames.Contains(baselineName))
            {
                skipped.Add(new CorpusSkip(relativePath, MissingBaselineSkipReason));
                continue;
            }
            if (parsed.Units.Count != 1)
            {
                skipped.Add(new CorpusSkip(relativePath, MultiFileSkipReason));
                continue;
            }
            if (parsed.Symlinks.Count != 0)
            {
                skipped.Add(new CorpusSkip(relativePath, "symlink"));
                continue;
            }

            eligible.Add(new CuratedConformanceCase(relativePath, baselineName));
        }

        if (debugWaveCap is int waveCap && eligible.Count > waveCap)
        {
            var sampled = new List<CuratedConformanceCase>(waveCap);
            var chosen = new HashSet<int>();
            double stride = (double)eligible.Count / waveCap;
            for (int i = 0; i < waveCap; i++)
            {
                chosen.Add((int)(i * stride));
            }
            for (int i = 0; i < eligible.Count; i++)
            {
                if (chosen.Contains(i))
                {
                    sampled.Add(eligible[i]);
                }
                else
                {
                    skipped.Add(new CorpusSkip(eligible[i].RelativePath, DebugWaveCapSkipReason));
                }
            }
            eligible = sampled;
        }

        return new CorpusSelection(eligible, skipped, files.Count);
    }

    public static ConformanceRunSummary RunCorpus()
    {
        return RunCorpus(Selection);
    }

    // TSCS_CONFORMANCE_SHARD="i/n" (1-based) restricts RunCorpus to every n-th selected case
    // starting at i-1. A full measurement is the union of shards 1/n..n/n run in separate
    // processes; a process death loses one shard, not the whole run.
    public static (int Index, int Count)? Shard
    {
        get
        {
            string? raw = Environment.GetEnvironmentVariable("TSCS_CONFORMANCE_SHARD");
            if (raw is null)
            {
                return null;
            }
            string[] parts = raw.Split('/');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int index) &&
                int.TryParse(parts[1], out int count) &&
                count > 0 && index >= 1 && index <= count)
            {
                return (index, count);
            }
            throw new InvalidOperationException($"TSCS_CONFORMANCE_SHARD must be 'i/n' with 1<=i<=n, got '{raw}'");
        }
    }

    // TSCS_CONFORMANCE_JOURNAL=<path>: append one line per finished case ("ok <name>" /
    // "fail <name>\t<failure>") so a dying process still leaves its per-case results behind.
    private static readonly string? s_journalPath =
        Environment.GetEnvironmentVariable("TSCS_CONFORMANCE_JOURNAL");

    public static ConformanceRunSummary RunCorpus(CorpusSelection selection)
    {
        IReadOnlyList<CuratedConformanceCase> cases = selection.Selected;
        if (Shard is (int shardIndex, int shardCount))
        {
            cases = cases.Where((_, i) => i % shardCount == shardIndex - 1).ToList();
        }
        var results = new List<ConformanceCaseResult>(cases.Count);
        foreach (CuratedConformanceCase testCase in cases)
        {
            // RunOne is individually crash-guarded (LargeStackExecutor + catch-all): a stub
            // NotImplementedException or deep-recursion failure becomes a FAIL result for that
            // case only and never takes down the batch.
            ConformanceCaseResult result = CompilerBaselineRunner.RunOne(testCase);
            results.Add(result);
            if (s_journalPath is not null)
            {
                File.AppendAllText(s_journalPath, result.Matched
                    ? $"ok {result.Name}\n"
                    : $"fail {result.Name}\t{(result.Failure ?? "errors.txt baseline mismatch").ReplaceLineEndings(" ")}\n");
            }
        }
        return new ConformanceRunSummary(results);
    }

    public static IReadOnlyList<CorpusDirectoryRate> GetWorstDirectoryRates(
        CorpusSelection selection,
        ConformanceRunSummary summary,
        int count)
    {
        var resultsByName = summary.Results.ToDictionary(
            result => result.Name,
            result => result,
            StringComparer.Ordinal);
        return selection.Selected
            .GroupBy(testCase => GetTopLevelDirectory(testCase.RelativePath), StringComparer.Ordinal)
            .Select(group => new CorpusDirectoryRate(
                group.Key,
                group.Count(testCase => resultsByName[CompilerBaselineRunner.GetCaseName(testCase.BaselineName)].Matched),
                group.Count()))
            .OrderBy(rate => (double)rate.Matched / rate.Total)
            .ThenByDescending(rate => rate.Total)
            .ThenBy(rate => rate.Directory, StringComparer.Ordinal)
            .Take(count)
            .ToList();
    }

    // Root-cause clustering for the summary report. Exception failures cluster on
    // "Type: message @ frame" (see CompilerBaselineRunner.DescribeException); pure baseline
    // mismatches are split by expected-vs-actual diagnostic counts, where the expected count is
    // recovered from the reference errors.txt itself: every diagnostic appears once in the
    // header list ("error TS...") and once inline ("!!! error TS..."), so
    // expected = total("error TS") - total("!!! error TS").
    public static IReadOnlyList<(string Cluster, int Count)> ClusterFailures(ConformanceRunSummary summary)
    {
        var byName = Selection.Selected.ToDictionary(
            testCase => CompilerBaselineRunner.GetCaseName(testCase.BaselineName),
            testCase => testCase,
            StringComparer.Ordinal);
        var clusters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ConformanceCaseResult result in summary.Results)
        {
            if (result.Matched)
            {
                continue;
            }
            string key = ClusterKey(result, byName.GetValueOrDefault(result.Name));
            clusters[key] = clusters.GetValueOrDefault(key) + 1;
        }
        // map-iter: 已排序 — descending count, then ordinal key.
        return clusters
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (entry.Key, entry.Value))
            .ToList();
    }

    private static string ClusterKey(ConformanceCaseResult result, CuratedConformanceCase? testCase)
    {
        string failure = result.Failure ?? "";
        // Exception failures carry "Type: message[ @ frame]" (possibly joined with a baseline
        // message via " | "); cluster on the exception segment alone.
        int separator = failure.IndexOf(" | ", StringComparison.Ordinal);
        string first = separator >= 0 ? failure[..separator] : failure;
        if (first.Contains("Exception", StringComparison.Ordinal))
        {
            return first;
        }

        int expected = -1;
        if (testCase is not null)
        {
            try
            {
                string referenceText = File.ReadAllText(
                    Path.Combine(Baseline.ReferenceRoot, "submodule", "conformance", testCase.BaselineName),
                    Encoding.UTF8);
                expected = CountOccurrences(referenceText, "error TS") -
                    CountOccurrences(referenceText, "!!! error TS");
            }
            catch (IOException)
            {
                // fall through to the generic bucket
            }
        }
        if (expected < 0)
        {
            return "errors.txt mismatch";
        }
        if (result.DiagnosticCount == expected)
        {
            return "errors.txt mismatch: same diagnostic count, text differs";
        }
        return result.DiagnosticCount < expected
            ? "errors.txt mismatch: fewer diagnostics than baseline"
            : "errors.txt mismatch: more diagnostics than baseline";
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string GetBaseName(string relativePath)
    {
        int slash = relativePath.LastIndexOf('/');
        return slash >= 0 ? relativePath[(slash + 1)..] : relativePath;
    }

    private static string GetTopLevelDirectory(string relativePath)
    {
        int slash = relativePath.IndexOf('/');
        return slash >= 0 ? relativePath[..slash] : ".";
    }

    // Mirrors tsgo harnessutil.SkipUnsupportedCompilerOptions. Vary-by lists are classified as
    // unsupported only when every configured value is unsupported; mixed lists remain in the
    // missing-plain-baseline bucket because they have configured baseline names instead.
    private static bool HasUnsupportedCompilerOptions(IReadOnlyDictionary<string, string> options)
    {
        return AllOptionValuesMatch(options, "module", "amd", "umd", "system") ||
            AllOptionValuesMatch(options, "moduleresolution", "node10", "classic") ||
            AllOptionValuesMatch(options, "esmoduleinterop", "false") ||
            AllOptionValuesMatch(options, "allowsyntheticdefaultimports", "false") ||
            HasNonEmptyOption(options, "baseurl") ||
            HasNonEmptyOption(options, "outfile") ||
            AllOptionValuesMatch(options, "target", "es5") ||
            AllOptionValuesMatch(options, "alwaysstrict", "false");
    }

    private static bool AllOptionValuesMatch(
        IReadOnlyDictionary<string, string> options,
        string optionName,
        params string[] unsupportedValues)
    {
        if (!options.TryGetValue(optionName, out string? value))
        {
            return false;
        }
        string[] values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return values.Length != 0 && values.All(candidate => unsupportedValues.Any(unsupported =>
            string.Equals(candidate, unsupported, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasNonEmptyOption(IReadOnlyDictionary<string, string> options, string optionName)
    {
        return options.TryGetValue(optionName, out string? value) && value.Length != 0;
    }

    private static string TrimTsExtension(string basename)
    {
        if (basename.EndsWith(".tsx", StringComparison.Ordinal))
        {
            return basename[..^4];
        }
        return basename.EndsWith(".ts", StringComparison.Ordinal) ? basename[..^3] : basename;
    }
}

// failingCases.txt bookkeeping — the corpus analogue of tsgo's fourslash failingTests.txt
// (plan/08 §7.1): one case name per line, '#' comments and blank lines ignored. A listed case
// Skips its theory when the byte comparison fails; an unlisted case must match, so checker
// regressions fail loudly while WIP cases stay visible-but-green.
public static class FailingCases
{
    public static string FilePath { get; } = Path.Combine(
        RepoPaths.RootPath, "tests", "Ts.Conformance.Tests", "failingCases.txt");

    private static readonly Lazy<HashSet<string>> s_names = new(
        () =>
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (!File.Exists(FilePath))
            {
                return names;
            }
            foreach (string rawLine in File.ReadAllLines(FilePath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                names.Add(line);
            }
            return names;
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static HashSet<string> Names => s_names.Value;

    private const string Header =
        """
        # Conformance corpus failing-cases allowlist (checker WIP; errors.txt byte-baseline mismatches).
        # One case name per line (= baseline name without .errors.txt). '#' and blank lines are ignored.
        # Semantics: listed + mismatch => theory Skips; NOT listed + mismatch => theory FAILS (regression);
        # listed + match => passes (prune opportunistically, the list only widens the Skip gate).
        # Regenerate (run from repo root; rewrites this file from the full eligible corpus).
        # Use the xUnit v3 native executable — under `dotnet test`/vstest the corpus theory can
        # collapse (no data pre-enumeration) and captured console output is swallowed:
        #   dotnet build tests/Ts.Conformance.Tests/Ts.Conformance.Tests.csproj -c Release /nodeReuse:false
        #   TSCS_CONFORMANCE_UPDATE_FAILING=1 tests/Ts.Conformance.Tests/bin/Release/net10.0/Ts.Conformance.Tests \
        #     -method "Ts.Conformance.Tests.CorpusRunnerTests.CorpusSummary_ReportsMatchRateAndAccounting"
        """;

    public static void Save(IEnumerable<string> names)
    {
        var builder = new StringBuilder();
        builder.Append(Header.ReplaceLineEndings("\n"));
        builder.Append('\n');
        foreach (string name in names.OrderBy(name => name, StringComparer.Ordinal))
        {
            builder.Append(name);
            builder.Append('\n');
        }
        File.WriteAllText(FilePath, builder.ToString());
    }
}
