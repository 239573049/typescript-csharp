// Ported from tsgo internal/testrunner/test_case_parser.go — the pure-text multi-file
// splitter shared by conformance and fourslash (plan/08-testing-strategy.md §4.3).
//
// SCOPE: only the text-parsing half is ported here. makeUnitsFromTest (which detects the
// tsconfig unit and runs the real JSON parser + tsoptions.ParseJsonSourceFileConfigFileContent)
// depends on compiler.Program/tsoptions and lands in phase 4; it is intentionally omitted.
//
// PORT-NOTES:
//   - Go's RE2 \s and \w are ASCII-only ([\t\n\f\r ] and [0-9A-Za-z_]); .NET's are Unicode by
//     default. The regexes below spell the ASCII classes out so matching is byte-identical to
//     Go on any input (a diff-load-bearing detail per §4.3).
//   - strings.ToLower on the ([0-9A-Za-z_]+) option name -> AsciiToLower (String.ToLowerInvariant
//     is banned; ASCII-only path is the documented alternative).
//   - parseFile's Go `(T, error)` return -> a delegate that may throw; the exception is caught
//     and surfaced as ParseTestFilesResult.Error, preserving Go's break-and-return-partial flow.
//   - The Go quirk where `... && currentFileName != ""` inside the first-marker `else` branch is
//     always false (the else is only entered when currentFileName == "") is preserved verbatim.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
// Class name collides with the last segment of its namespace; alias like Scanner.cs does.
using Scanner = Ts.Scanner.Scanner;

namespace Ts.TestUtil;

// Go: parseFile func(filename, content string, fileOptions map[string]string) (T, error).
// A thrown exception stands in for a non-nil error return.
public delegate T ParseFileFunc<out T>(
    string fileName,
    string content,
    IReadOnlyDictionary<string, string> fileOptions);

// Go: ParseTestFilesOptions
public struct ParseTestFilesOptions
{
    // If true, allows test content to appear before the first @Filename directive.
    // In this case, an implicit first file is created using the fileName parameter.
    // This matches the behavior of the TypeScript fourslash test harness.
    public bool AllowImplicitFirstFile { get; set; }
}

// Go returns (units, symlinks, currentDir, globalOptions, error) — bundled here.
public readonly record struct ParseTestFilesResult<T>(
    List<T> Units,
    Dictionary<string, string> Symlinks,
    string CurrentDirectory,
    Dictionary<string, string> GlobalOptions,
    Exception? Error);

public static partial class TestCaseParser
{
    // Go: var lineDelimiter = regexp.MustCompile("\r?\n")
    [GeneratedRegex("\r?\n")]
    private static partial Regex LineDelimiterRegex();

    // Go: var optionRegex = regexp.MustCompile(`(?m)^\/{2}\s*@(\w+)\s*:\s*([^\r\n]*)`)
    // \s -> [\t\n\f\r ] and \w -> [0-9A-Za-z_] to match Go's ASCII RE2 classes exactly.
    [GeneratedRegex(@"^//[\t\n\f\r ]*@([0-9A-Za-z_]+)[\t\n\f\r ]*:[\t\n\f\r ]*([^\r\n]*)", RegexOptions.Multiline)]
    private static partial Regex OptionRegex();

    // Go: var linkRegex = regexp.MustCompile(`(?m)^\/{2}\s*@link\s*:\s*([^\r\n]*)\s*->\s*([^\r\n]*)`)
    [GeneratedRegex(@"^//[\t\n\f\r ]*@link[\t\n\f\r ]*:[\t\n\f\r ]*([^\r\n]*)[\t\n\f\r ]*->[\t\n\f\r ]*([^\r\n]*)", RegexOptions.Multiline)]
    private static partial Regex LinkRegex();

    // File-specific directives used by fourslash tests
    private static readonly string[] s_fourslashDirectives = { "emitthisfile" };

    // Given a test file containing // @FileName and // @symlink directives,
    // return an array of named units of code to be added to an existing compiler instance,
    // along with a map of symlinks and the current directory.
    public static ParseTestFilesResult<T> ParseTestFilesAndSymlinks<T>(
        string code,
        string fileName,
        ParseFileFunc<T> parseFile)
    {
        return ParseTestFilesAndSymlinksWithOptions(code, fileName, parseFile, default);
    }

    public static ParseTestFilesResult<T> ParseTestFilesAndSymlinksWithOptions<T>(
        string code,
        string fileName,
        ParseFileFunc<T> parseFile,
        ParseTestFilesOptions options)
    {
        // List of all the subfiles we've parsed out
        var testUnits = new List<T>();

        string[] lines = LineDelimiterRegex().Split(code);

        // Stuff related to the subfile we're parsing
        var currentFileContent = new StringBuilder();
        string currentFileName = "";
        bool seenContentLine = false;
        bool hasSeenFile = false;
        if (options.AllowImplicitFirstFile)
        {
            // For fourslash tests, initialize currentFileName to the fileName parameter
            // so content before the first @Filename directive goes into an implicit first file
            currentFileName = fileName;
        }
        string currentDirectory = "";
        Exception? parseError = null;
        var currentFileOptions = new Dictionary<string, string>(StringComparer.Ordinal);
        var symlinks = new Dictionary<string, string>(StringComparer.Ordinal);
        var globalOptions = new Dictionary<string, string>(StringComparer.Ordinal);

        // Go: `newTestFile, e := parseFile(...); if e != nil { parseError = e; break } else append`.
        // Returns false (caller should break) if parseFile threw.
        bool TryParseAndAppend(string name, string content, Dictionary<string, string> opts)
        {
            try
            {
                testUnits.Add(parseFile(name, content, opts));
                return true;
            }
            catch (Exception e)
            {
                parseError = e;
                return false;
            }
        }

        foreach (string line in lines)
        {
            bool ok = ParseSymlinkFromTest(line, symlinks);
            if (ok)
            {
                continue;
            }
            Match testMetaData = OptionRegex().Match(line);
            if (testMetaData.Success)
            {
                // Comment line, check for global/file @options and record them
                string metaDataName = AsciiToLower(testMetaData.Groups[1].Value);
                string metaDataValue = testMetaData.Groups[2].Value.Trim();
                if (metaDataName == "currentdirectory")
                {
                    currentDirectory = metaDataValue;
                }
                if (metaDataName != "filename")
                {
                    if (metaDataName == "symlink" && currentFileName != "")
                    {
                        foreach (string rawLink in metaDataValue.Split(','))
                        {
                            string link = rawLink.Trim();
                            if (link != "")
                            {
                                symlinks[link] = currentFileName;
                            }
                        }
                    }
                    else if (Array.IndexOf(s_fourslashDirectives, metaDataName) >= 0)
                    {
                        // File-specific option
                        currentFileOptions[metaDataName] = metaDataValue;
                    }
                    else
                    {
                        // Global option
                        if (globalOptions.TryGetValue(metaDataName, out string? existingValue) && existingValue != metaDataValue)
                        {
                            // !!! This would break existing submodule tests
                            // panic("Duplicate global option: " + metaDataName)
                        }
                        globalOptions[metaDataName] = metaDataValue;
                    }
                    continue;
                }

                // New metadata statement after having collected some code to go with the previous metadata
                if (currentFileName != "")
                {
                    // Store result file - always save for regular tests, but skip empty implicit first file for fourslash
                    bool shouldSaveFile = !options.AllowImplicitFirstFile || currentFileContent.Length != 0 || hasSeenFile;
                    if (shouldSaveFile)
                    {
                        hasSeenFile = true;
                        if (!TryParseAndAppend(currentFileName, currentFileContent.ToString(), currentFileOptions))
                        {
                            break;
                        }
                    }

                    // Reset local data
                    currentFileContent.Clear();
                    seenContentLine = false;
                    currentFileName = metaDataValue;
                    currentFileOptions = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                else
                {
                    // First metadata marker in the file
                    bool hasContentBeforeFirstFilename = currentFileContent.Length != 0 &&
                        global::Ts.Scanner.Scanner.SkipTrivia(currentFileContent.ToString(), 0) != currentFileContent.Length;
                    if (hasContentBeforeFirstFilename && !options.AllowImplicitFirstFile)
                    {
                        throw new InvalidOperationException("Non-comment test content appears before the first '// @Filename' directive");
                    }

                    // If we have content before the first @Filename and AllowImplicitFirstFile is true,
                    // we need to save it as an implicit first file before starting the new file
                    if (hasContentBeforeFirstFilename && options.AllowImplicitFirstFile && currentFileName != "")
                    {
                        // Store the implicit first file
                        hasSeenFile = true;
                        if (!TryParseAndAppend(currentFileName, currentFileContent.ToString(), currentFileOptions))
                        {
                            break;
                        }
                    }

                    // Reset for the new file
                    currentFileContent.Clear();
                    seenContentLine = false;
                    currentFileName = testMetaData.Groups[2].Value.Trim();
                    currentFileOptions = new Dictionary<string, string>(StringComparer.Ordinal);
                }
            }
            else
            {
                // Subfile content line
                // Append to the current subfile content, inserting a newline if needed
                // For fourslash tests, use seenContentLine to preserve leading blank lines
                // (matching TS fourslash's //// content markers). For compiler tests, use
                // Len() != 0 which drops leading blanks (matching TS's harness behavior).
                if (options.AllowImplicitFirstFile)
                {
                    if (seenContentLine)
                    {
                        currentFileContent.Append('\n');
                    }
                    seenContentLine = true;
                }
                else
                {
                    if (currentFileContent.Length != 0)
                    {
                        currentFileContent.Append('\n');
                    }
                }
                currentFileContent.Append(line);
            }
        }

        // normalize the fileName for the single file case
        if (testUnits.Count == 0 && currentFileName.Length == 0)
        {
            currentFileName = global::Ts.TsPath.TsPath.GetBaseFileName(fileName);
        }

        // if there are no parse errors so far, parse the rest of the file
        if (parseError == null)
        {
            // EOF, push whatever remains
            try
            {
                testUnits.Add(parseFile(currentFileName, currentFileContent.ToString(), currentFileOptions));
            }
            catch (Exception e)
            {
                parseError = e;
                // Go appends newTestFile2 unconditionally (the zero value on error).
                testUnits.Add(default!);
            }
        }

        return new ParseTestFilesResult<T>(testUnits, symlinks, currentDirectory, globalOptions, parseError);
    }

    // Go: extractCompilerSettings — grabs all @option pairs up front (value's trailing ';' stripped).
    public static Dictionary<string, string> ExtractCompilerSettings(string content)
    {
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in OptionRegex().Matches(content))
        {
            opts[AsciiToLower(match.Groups[1].Value)] = TrimSuffix(match.Groups[2].Value.Trim(), ';');
        }

        return opts;
    }

    private static bool ParseSymlinkFromTest(string line, Dictionary<string, string> symlinks)
    {
        Match linkMetaData = LinkRegex().Match(line);
        if (!linkMetaData.Success)
        {
            return false;
        }

        symlinks[linkMetaData.Groups[2].Value.Trim()] = linkMetaData.Groups[1].Value.Trim();
        return true;
    }

    // Go: strings.TrimSuffix(s, ";") — single-char suffix, removed once if present.
    private static string TrimSuffix(string s, char suffix)
    {
        if (s.Length > 0 && s[s.Length - 1] == suffix)
        {
            return s.Substring(0, s.Length - 1);
        }
        return s;
    }

    // Go: strings.ToLower over an ASCII ([0-9A-Za-z_]+) option name. String.ToLowerInvariant is
    // banned; a proven ASCII-only lowercase is byte-identical here (BannedSymbols.txt guidance).
    private static string AsciiToLower(string s)
    {
        int i = 0;
        while (i < s.Length && !(s[i] >= 'A' && s[i] <= 'Z'))
        {
            i++;
        }
        if (i == s.Length)
        {
            return s;
        }
        char[] chars = s.ToCharArray();
        for (; i < chars.Length; i++)
        {
            if (chars[i] >= 'A' && chars[i] <= 'Z')
            {
                chars[i] = (char)(chars[i] + 32);
            }
        }
        return new string(chars);
    }
}
