// Ported from tsgo internal/testutil/baseline/baseline.go.
//
// Byte-exact baseline comparison (writes actual output to baselines/local, compares against
// baselines/reference) plus, for submodule mode, a patience-diff .diff artifact against the
// Strada reference (plan/08-testing-strategy.md §4.6, §4.7). The accept workflow is external:
// tools/baseline-accept copies local -> reference (§4.8), so the reference tree stays read-only
// during test runs (no UPDATE_BASELINE=1 direct-write path).
//
// PORT-NOTES:
//   - *testing.T -> IBaselineTestContext (t.Error/t.Fatal shim); wired to xUnit in phase 4.
//   - os.ReadFile/os.WriteFile use raw bytes; here we go through UTF-8-no-BOM explicitly so
//     the byte image matches Go (no BOM stripping on read, none emitted on write).
//   - filepath.Join -> Path.Combine (inputs are simple relative subpaths; no cleaning needed).
//     diffKey deliberately keeps a literal "/" separator to match submoduleAccepted.txt keys.
//   - github.com/peter-evans/patience -> Ts.TestUtil.Diff.Patience (§4.6 byte-for-byte port).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Ts.Collections;
using Ts.TestUtil.Diff;
// Class name collides with the last segment of its namespace; alias like Scanner.cs does.
using StringUtil = Ts.StringUtil.StringUtil;

namespace Ts.TestUtil;

public static partial class Baseline
{
    // Go: Options
    public sealed class Options
    {
        public string Subfolder { get; init; } = "";
        public bool IsSubmodule { get; init; }
        public bool IsSubmoduleAccepted { get; init; }
        public bool IsSubmoduleTriaged { get; init; }
        public Func<string, string>? DiffFixupOld { get; init; }
        public Func<string, string>? DiffFixupNew { get; init; }
        public bool SkipDiffWithOld { get; init; }
    }

    public const string NoContent = "<no content>";

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Go: package-level vars, initialized once.
    public static string LocalRoot { get; } = Path.Combine(RepoPaths.TestDataPath, "baselines", "local");

    public static string ReferenceRoot { get; } = Path.Combine(RepoPaths.TestDataPath, "baselines", "reference");

    public static string SubmoduleReferenceRoot { get; } =
        Path.Combine(RepoPaths.TypeScriptSubmodulePath, "tests", "baselines", "reference");

    public static void Run(IBaselineTestContext t, string fileName, string actual, Options opts)
    {
        string origSubfolder = opts.Subfolder;

        {
            string subfolder = opts.Subfolder;
            if (opts.IsSubmodule)
            {
                subfolder = Path.Combine("submodule", subfolder);
            }

            string localPath = Path.Combine(LocalRoot, subfolder, fileName);
            string referencePath = Path.Combine(ReferenceRoot, subfolder, fileName);

            // Record this baseline for tracking unused baselines
            BaselineTracker.RecordBaseline(t, Path.Combine(subfolder, fileName));

            WriteComparison(t, actual, localPath, referencePath, false);
        }

        if (!opts.IsSubmodule || opts.SkipDiffWithOld)
        {
            // Not a submodule, no diffs.
            return;
        }

        string submoduleReference = Path.Combine(SubmoduleReferenceRoot, fileName);
        string submoduleExpected = ReadFileOrNoContent(submoduleReference);

        const string submoduleFolder = "submodule";
        const string submoduleAcceptedFolder = "submoduleAccepted";
        const string submoduleTriagedFolder = "submoduleTriaged";

        string diffFileName = fileName + ".diff";
        string diffKey = origSubfolder + "/" + diffFileName;
        bool isSubmoduleAccepted = opts.IsSubmoduleAccepted || SubmoduleAcceptedFileNames.Has(diffKey);
        bool isSubmoduleTriaged = opts.IsSubmoduleTriaged || SubmoduleTriagedFileNames.Has(diffKey);

        if (isSubmoduleAccepted && isSubmoduleTriaged)
        {
            t.Fatal($"diff file {origSubfolder}/{diffFileName} is in both submoduleAccepted and submoduleTriaged; it should only be in one");
        }

        string outRoot;
        if (isSubmoduleAccepted)
        {
            outRoot = submoduleAcceptedFolder;
        }
        else if (isSubmoduleTriaged)
        {
            outRoot = submoduleTriagedFolder;
        }
        else
        {
            outRoot = submoduleFolder;
        }

        string[] allRoots = { submoduleFolder, submoduleAcceptedFolder, submoduleTriagedFolder };

        string diff = GetBaselineDiff(actual, submoduleExpected, fileName, opts.DiffFixupOld, opts.DiffFixupNew);

        foreach (string root in allRoots)
        {
            string localPath = Path.Combine(LocalRoot, root, origSubfolder, diffFileName);
            string referencePath = Path.Combine(ReferenceRoot, root, origSubfolder, diffFileName);

            // Record this baseline for tracking unused baselines
            BaselineTracker.RecordBaseline(t, Path.Combine(root, origSubfolder, diffFileName));

            if (root == outRoot)
            {
                WriteComparison(t, diff, localPath, referencePath, false);
            }
            else
            {
                WriteComparison(t, NoContent, localPath, referencePath, false);
            }
        }
    }

    // Go: sync.OnceValue -> Lazy (ExecutionAndPublication thread safety).
    private static readonly Lazy<Set<string>> s_submoduleAcceptedFileNames = new(() =>
        ReadFileNameSet(Path.Combine(RepoPaths.TestDataPath, "submoduleAccepted.txt")));

    private static readonly Lazy<Set<string>> s_submoduleTriagedFileNames = new(() =>
        ReadFileNameSet(Path.Combine(RepoPaths.TestDataPath, "submoduleTriaged.txt")));

    public static Set<string> SubmoduleAcceptedFileNames => s_submoduleAcceptedFileNames.Value;

    public static Set<string> SubmoduleTriagedFileNames => s_submoduleTriagedFileNames.Value;

    private static Set<string> ReadFileNameSet(string path)
    {
        var set = new Set<string>();

        if (TryReadFile(path, out string content))
        {
            foreach (string rawLine in content.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                set.Add(line);
            }
        }
        else
        {
            throw new InvalidOperationException($"failed to read file {path}");
        }

        return set;
    }

    private static string ReadFileOrNoContent(string fileName)
    {
        if (!TryReadFile(fileName, out string content))
        {
            return NoContent;
        }
        return content;
    }

    public static string DiffText(string oldName, string newName, string expected, string actual)
    {
        List<DiffLine> lines = Patience.Diff(global::Ts.StringUtil.StringUtil.SplitLines(expected), global::Ts.StringUtil.StringUtil.SplitLines(actual));
        return Patience.UnifiedDiffTextWithOptions(lines, new UnifiedDiffOptions
        {
            Precontext = 3,
            Postcontext = 3,
            SrcHeader = oldName,
            DstHeader = newName,
        });
    }

    // Go: getBaselineDiff (the unused *testing.T parameter is dropped; Go never calls it).
    public static string GetBaselineDiff(
        string actual,
        string expected,
        string fileName,
        Func<string, string>? fixupOld,
        Func<string, string>? fixupNew)
    {
        if (fixupOld is not null)
        {
            expected = fixupOld(expected);
        }
        if (fixupNew is not null)
        {
            actual = fixupNew(actual);
        }
        if (actual == expected)
        {
            return NoContent;
        }
        string s = DiffText("old." + fileName, "new." + fileName, expected, actual);

        // If the diff is empty (just headers, no hunks), return NoContent
        if (!s.Contains("@@", StringComparison.Ordinal))
        {
            return NoContent;
        }

        // Remove line numbers from unified diff headers; this avoids adding/deleting
        // lines in our baselines from causing knock-on header changes later in the diff.

        int aCurLine = 1;
        int bCurLine = 1;
        s = FixUnifiedDiffRegex().Replace(s, match =>
        {
            int aLine = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int bLine = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            int aDiff = aLine - aCurLine;
            int bDiff = bLine - bCurLine;
            aCurLine = aLine;
            bCurLine = bLine;

            // Keep surrounded by @@, to make GitHub's grammar happy.
            // https://github.com/textmate/diff.tmbundle/blob/0593bb775eab1824af97ef2172fd38822abd97d7/Syntaxes/Diff.plist#L68
            return string.Create(CultureInfo.InvariantCulture, $"@@= skipped -{aDiff}, +{bDiff} lines =@@");
        });

        return s;
    }

    // Go: var fixUnifiedDiff = regexp.MustCompile(`@@ -\d+,\d+ \+\d+,\d+ @@`).
    // Capture groups are added so the four counts can be read without a second Sscanf pass;
    // the overall matched span is identical to Go's non-capturing pattern.
    [GeneratedRegex(@"@@ -([0-9]+),([0-9]+) \+([0-9]+),([0-9]+) @@")]
    private static partial Regex FixUnifiedDiffRegex();

    public static void RunAgainstSubmodule(IBaselineTestContext t, string fileName, string actual, Options opts)
    {
        // Record this baseline for tracking unused baselines
        BaselineTracker.RecordBaseline(t, Path.Combine(opts.Subfolder, fileName));

        string local = Path.Combine(LocalRoot, opts.Subfolder, fileName);
        string reference = Path.Combine(SubmoduleReferenceRoot, opts.Subfolder, fileName);
        WriteComparison(t, actual, local, reference, true);
    }

    public static void WriteComparison(
        IBaselineTestContext t,
        string actualContent,
        string local,
        string reference,
        bool comparingAgainstSubmodule)
    {
        if (actualContent == "")
        {
            throw new InvalidOperationException(
                "the generated content was \"\". Return 'baseline.NoContent' if no baselining is required.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        }
        catch (IOException err)
        {
            t.Error($"failed to create directories for the local baseline file {local}: {err.Message}");
            return;
        }

        if (File.Exists(local))
        {
            try
            {
                File.Delete(local);
            }
            catch (IOException err)
            {
                t.Error($"failed to remove the local baseline file {local}: {err.Message}");
                return;
            }
        }

        string expected = NoContent;
        bool foundExpected = false;
        if (TryReadFile(reference, out string content))
        {
            expected = content;
            foundExpected = true;
        }

        if (expected != actualContent || (actualContent == NoContent && foundExpected))
        {
            if (actualContent == NoContent)
            {
                string deletePath = local + ".delete";
                try
                {
                    File.WriteAllBytes(deletePath, Array.Empty<byte>());
                }
                catch (IOException err)
                {
                    t.Error($"failed to write the local baseline file {deletePath}: {err.Message}");
                    return;
                }
            }
            else
            {
                try
                {
                    WriteFileText(local, actualContent);
                }
                catch (IOException err)
                {
                    t.Error($"failed to write the local baseline file {local}: {err.Message}");
                    return;
                }
            }

            if (!File.Exists(reference))
            {
                if (comparingAgainstSubmodule)
                {
                    t.Error($"the baseline file {reference} does not exist in the TypeScript submodule");
                }
                else
                {
                    t.Error($"new baseline created at {local}.");
                }
            }
            else if (comparingAgainstSubmodule)
            {
                t.Error($"the baseline file {reference} does not match the reference in the TypeScript submodule");
            }
            else
            {
                // Go says "Run `hereby baseline-accept`"; the C# accept workflow is
                // `dotnet run --project tools/baseline-accept` (plan/08 §4.8).
                t.Error($"the baseline file {reference} has changed. (Run `dotnet run --project tools/baseline-accept` if the new baseline is correct.)");
            }
        }
    }

    // Go: os.ReadFile + string(content). Reads raw bytes and decodes UTF-8 without stripping
    // a BOM (matches Go's byte->string conversion). Returns false on any read failure.
    private static bool TryReadFile(string path, out string content)
    {
        try
        {
            content = s_utf8NoBom.GetString(File.ReadAllBytes(path));
            return true;
        }
        catch (IOException)
        {
            content = "";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            content = "";
            return false;
        }
    }

    // Go: os.WriteFile(path, []byte(content), 0o644) — exact UTF-8 bytes, no BOM.
    private static void WriteFileText(string path, string content)
    {
        File.WriteAllBytes(path, s_utf8NoBom.GetBytes(content));
    }
}
