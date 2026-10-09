// Ported from tsgo internal/fourslash/baselineutil.go (:23-97, :95-501) — the baseline-command
// registry: the command name constants, the section header writer, the baseline
// file-name/extension mapping, normalizeCommandName, and getBaselineOptions with the
// submodule DiffFixupOld/DiffFixupNew closures (Strada-format text fixups keyed to the
// TypeScript submodule reference).
//
// PORT-NOTE: the fixups are textual by construction (Go rewrites the reference/actual baseline
// text before diffing); they are the one place where a byte-for-byte port of *baseline text*
// shape is unavoidable. They are inert unless a producer's actual output is compared against the
// submodule reference (the diff artifact only changes when the compared texts differ), so they
// are unverified until their producers land.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Ts.TestUtil.Fourslash;

// baselineutil.go:23-45 — baselineCommand and the command constants.
public readonly record struct FourslashBaselineCommand(string Value)
{
    public static readonly FourslashBaselineCommand AutoImports = new("Auto Imports");
    public static readonly FourslashBaselineCommand CallHierarchy = new("Call Hierarchy");
    public static readonly FourslashBaselineCommand ClosingTag = new("Closing Tag");
    public static readonly FourslashBaselineCommand DocumentHighlights = new("documentHighlights");
    public static readonly FourslashBaselineCommand FindAllReferences = new("findAllReferences");
    public static readonly FourslashBaselineCommand VsFindAllReferences = new("vsFindAllReferences");
    public static readonly FourslashBaselineCommand GoToDefinition = new("goToDefinition");
    public static readonly FourslashBaselineCommand GoToImplementation = new("goToImplementation");
    public static readonly FourslashBaselineCommand GoToSourceDefinition = new("goToSourceDefinition");
    public static readonly FourslashBaselineCommand GoToTypeDefinition = new("goToType");
    public static readonly FourslashBaselineCommand InlayHints = new("Inlay Hints");
    public static readonly FourslashBaselineCommand NonSuggestionDiagnostics = new("Syntax and Semantic Diagnostics");
    public static readonly FourslashBaselineCommand QuickInfo = new("QuickInfo");
    public static readonly FourslashBaselineCommand LinkedEditing = new("linkedEditing");
    public static readonly FourslashBaselineCommand Rename = new("findRenameLocations");
    public static readonly FourslashBaselineCommand SignatureHelp = new("SignatureHelp");
    public static readonly FourslashBaselineCommand SmartSelection = new("Smart Selection");
    public static readonly FourslashBaselineCommand CodeLenses = new("Code Lenses");
    public static readonly FourslashBaselineCommand DocumentSymbols = new("Document Symbols");
    public static readonly FourslashBaselineCommand WorkspaceSymbol = new("workspaceSymbol");

    // Go's named constant for the rename diff fixup command match (baselineutil.go:152).
    internal bool Matches(string commandName) => string.Equals(Value, commandName, StringComparison.Ordinal);

    public override string ToString() => Value;
}

// Per-session accumulator for the baselines map (Go: f.baselines map[baselineCommand]*strings.Builder).
public sealed class FourslashBaselines
{
    private readonly Dictionary<FourslashBaselineCommand, System.Text.StringBuilder> builders = new();

    public int Count => builders.Count;

    // baselineutil.go:47-65 — addResultToBaseline (minus the state-baseline branch, which the
    // session owns because it reroutes to its single state builder).
    public void AddResult(FourslashBaselineCommand command, string actual)
    {
        System.Text.StringBuilder b = GetOrAdd(command);
        if (b.Length != 0)
        {
            b.Append("\n\n\n\n");
        }
        b.Append("// === ").Append(command.Value).Append(" ===\n");
        b.Append(actual);
    }

    // baselineutil.go:67-74 — writeToBaseline.
    public void WriteTo(FourslashBaselineCommand command, string content)
    {
        GetOrAdd(command).Append(content);
    }

    public System.Text.StringBuilder GetOrAdd(FourslashBaselineCommand command)
    {
        if (!builders.TryGetValue(command, out System.Text.StringBuilder? b))
        {
            b = new System.Text.StringBuilder();
            builders[command] = b;
        }
        return b;
    }

    // map-iter: 已排序 — verifyBaselines flushes in command-name order so local baseline writes
    // are deterministic (Go ranges over the map, which randomizes; the C# BaselineTracker keys
    // on the file path, so a stable order cannot change which baselines are produced).
    public IEnumerable<(FourslashBaselineCommand Command, string Content)> Entries()
    {
        var commands = new List<FourslashBaselineCommand>(builders.Keys);
        commands.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
        foreach (FourslashBaselineCommand command in commands)
        {
            yield return (command, builders[command].ToString());
        }
    }
}

public static partial class FourslashBaselineUtil
{
    // baselineutil.go:76-78 — getBaselineFileName.
    public static string GetBaselineFileName(string testBaseName, FourslashBaselineCommand command) =>
        testBaseName + "." + GetBaselineExtension(command);

    // baselineutil.go:81-93 — getBaselineExtension.
    public static string GetBaselineExtension(FourslashBaselineCommand command)
    {
        return command.Value switch
        {
            "QuickInfo" or "SignatureHelp" or "Smart Selection" or "Inlay Hints"
                or "Syntax and Semantic Diagnostics" or "Document Symbols" or "Closing Tag"
                or "vsFindAllReferences" => "baseline",
            "Call Hierarchy" => "callHierarchy.txt",
            "Auto Imports" => "baseline.md",
            "linkedEditing" => "linkedEditing.txt",
            _ => "baseline.jsonc",
        };
    }

    // baselineutil.go:493-495 — isSubmoduleTest.
    public static bool IsSubmoduleTest(string testPath) =>
        testPath.Contains("fourslash/tests/gen", StringComparison.Ordinal) ||
        testPath.Contains("fourslash/tests/manual", StringComparison.Ordinal);

    // baselineutil.go:497-501 — normalizeCommandName. (Go: strings.Join + LowerFirstChar; the
    // first-char fold is ASCII here — command names are ASCII by construction, and the repo's
    // culture-sensitive API ban rules out ToLowerInvariant.)
    public static string NormalizeCommandName(string command)
    {
        string joined = string.Concat(command.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (joined.Length == 0 || joined[0] is not (>= 'A' and <= 'Z'))
        {
            return joined;
        }
        return (char)(joined[0] + ('a' - 'A')) + joined[1..];
    }

    // baselineutil.go:95-491 — getBaselineOptions.
    public static Baseline.Options GetBaselineOptions(FourslashBaselineCommand command, string testPath)
    {
        string subfolder = "fourslash/" + NormalizeCommandName(command.Value);
        if (!IsSubmoduleTest(testPath))
        {
            return new Baseline.Options { Subfolder = subfolder };
        }

        switch (command.Value)
        {
            case "Smart Selection":
                return new Baseline.Options { Subfolder = subfolder, IsSubmodule = true };

            case "Call Hierarchy":
                return new Baseline.Options
                {
                    Subfolder = subfolder,
                    IsSubmodule = true,
                    DiffFixupOld = static s =>
                    {
                        // TypeScript baselines have "/tests/cases/fourslash/" prefix in file paths
                        // Handle /server/ subdirectory - need to remove both prefixes
                        s = s.Replace("/tests/cases/fourslash/server/", "/", StringComparison.Ordinal);
                        s = s.Replace("/tests/cases/fourslash/", "/", StringComparison.Ordinal);
                        // SymbolKind enum differences between Strada and tsgo
                        s = s.Replace("kind: getter", "kind: property", StringComparison.Ordinal);
                        s = s.Replace("kind: script", "kind: file", StringComparison.Ordinal);
                        return s;
                    },
                };

            case "findRenameLocations":
                return new Baseline.Options
                {
                    Subfolder = subfolder,
                    IsSubmodule = true,
                    DiffFixupOld = static s =>
                    {
                        const string command = "findRenameLocations";
                        var commandLines = new List<string>();
                        var replacer = new[]
                        {
                            ("<|", ""), ("|>", ""),
                            ("/tests/cases/fourslash", ""), ("/server", ""),
                            ("providePrefixAndSuffixTextForRename", "useAliasesForRename"),
                        };
                        bool isInCommand = false;
                        foreach (string line in s.Split('\n'))
                        {
                            if (line.StartsWith("// @findInStrings: ", StringComparison.Ordinal) ||
                                line.StartsWith("// @findInComments: ", StringComparison.Ordinal))
                            {
                                continue;
                            }
                            Match m = CommandPrefixRegex().Match(line);
                            if (m.Success)
                            {
                                string commandName = m.Groups[1].Value;
                                isInCommand = commandName == command;
                            }
                            if (isInCommand)
                            {
                                commandLines.Add(ReplaceAll(line, replacer));
                            }
                        }
                        return string.Join("\n", DropTrailingEmptyLines(commandLines));
                    },
                };

            case "Inlay Hints":
                return new Baseline.Options
                {
                    Subfolder = subfolder,
                    IsSubmodule = true,
                    DiffFixupOld = InlayHintsFixupOld,
                    DiffFixupNew = InlayHintsFixupNew,
                };

            case "goToDefinition":
            case "goToType":
            case "goToImplementation":
            case "goToSourceDefinition":
                string definitionCommand = command.Value;
                return new Baseline.Options
                {
                    Subfolder = subfolder,
                    IsSubmodule = true,
                    DiffFixupOld = s =>
                    {
                        var commandLines = new List<string>();
                        bool isInCommand = false;
                        bool isInDetails = false;
                        foreach (string line in s.Split('\n'))
                        {
                            Match m = CommandPrefixRegex().Match(line);
                            if (m.Success)
                            {
                                isInDetails = false;
                                string commandName = m.Groups[1].Value;
                                isInCommand = commandName == definitionCommand ||
                                    definitionCommand == "goToDefinition" && commandName == "getDefinitionAtPosition";
                            }
                            if (isInCommand)
                            {
                                if (line.Contains("// === Details ===", StringComparison.Ordinal))
                                {
                                    // Drop blank line before details
                                    commandLines.RemoveAt(commandLines.Count - 1);
                                    isInDetails = true;
                                }
                                // We don't diff the details section, since the structure of responses is different.
                                if (!isInDetails)
                                {
                                    string fixedLine = ReplaceAll(line, new[]
                                    {
                                        ("/tests/cases/fourslash", ""),
                                        ("/server", ""),
                                        ("getDefinitionAtPosition", "goToDefinition"),
                                        ("/*GOTO DEF POS*/", "/*GOTO DEF*/"),
                                    });
                                    fixedLine = ObjectRangeRegex().Replace(fixedLine, "");
                                    commandLines.Add(fixedLine);
                                }
                                else if (line == "  ]")
                                {
                                    isInDetails = false;
                                }
                            }
                        }
                        return string.Join("\n", DropTrailingEmptyLines(commandLines));
                    },
                    DiffFixupNew = static s => s.Replace("bundled:///libs/", "", StringComparison.Ordinal),
                };

            case "findAllReferences":
                return new Baseline.Options
                {
                    Subfolder = subfolder,
                    IsSubmodule = true,
                    DiffFixupOld = FindAllReferencesFixupOld,
                };

            case "linkedEditing":
                static string DeleteInfo(string s)
                {
                    string[] lines = s.Split('\n');
                    bool inLinkedEditingInfo = false;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (LinkedEditingInfoHeaderRegex().IsMatch(lines[i]))
                        {
                            inLinkedEditingInfo = true;
                            continue;
                        }
                        if (FileNameHeaderRegex().IsMatch(lines[i]))
                        {
                            inLinkedEditingInfo = false;
                            continue;
                        }
                        // drop the info since it's different--linked editing positions should be verified
                        // by file content/markers
                        if (!inLinkedEditingInfo)
                        {
                            lines[i] = "";
                        }
                    }
                    // Go discards commandLines entirely and joins dropTrailingEmptyLines(nil) — "".
                    return string.Join("\n", DropTrailingEmptyLines(new List<string>()));
                }
                return new Baseline.Options
                {
                    Subfolder = subfolder,
                    IsSubmodule = true,
                    DiffFixupOld = DeleteInfo,
                    DiffFixupNew = DeleteInfo,
                };

            default:
                return new Baseline.Options { Subfolder = subfolder, IsSubmodule = true };
        }
    }

    // baselineutil.go:89-91 — dropTrailingEmptyLines. Go slices to the last non-empty index + 1;
    // an all-empty input yields an empty slice (Go's FindLastIndex returns -1). An empty slice
    // splits to the single element [""], which is what Go's strings.Join produces too.
    private static List<string> DropTrailingEmptyLines(List<string> ss)
    {
        int last = -1;
        for (int i = ss.Count - 1; i >= 0; i--)
        {
            if (ss[i] != "")
            {
                last = i;
                break;
            }
        }
        return ss.GetRange(0, last + 1);
    }

    private static string ReplaceAll(string line, (string Old, string New)[] pairs)
    {
        foreach ((string oldValue, string newValue) in pairs)
        {
            line = line.Replace(oldValue, newValue, StringComparison.Ordinal);
        }
        return line;
    }

    // baselineutil.go:149 etc. — ^// === ([a-z\sA-Z]*) ===
    [GeneratedRegex(@"^// === ([a-z\sA-Z]*) ===")]
    private static partial Regex CommandPrefixRegex();

    // baselineutil.go:289 — {\| [^|]* \|}
    [GeneratedRegex(@"{\| [^|]* \|}")]
    private static partial Regex ObjectRangeRegex();

    // baselineutil.go:480 — === [0-9]+ ===
    [GeneratedRegex(@"=== [0-9]+ ===")]
    private static partial Regex LinkedEditingInfoHeaderRegex();

    // baselineutil.go:481 — === [\w,\s-]+\.[A-Za-z]+ ===
    [GeneratedRegex(@"=== [\w,\s-]+\.[A-Za-z]+ ===")]
    private static partial Regex FileNameHeaderRegex();

    // baselineutil.go:283 — ^// === ([^ ]*) ===
    [GeneratedRegex(@"^// === ([^ ]*) ===")]
    private static partial Regex FilePrefixRegex();

    // baselineutil.go:200-282 — the inlayHints DiffFixupOld body.
    private static string InlayHintsFixupOld(string s)
    {
        const string command = "Inlay Hints";
        var commandLines = new List<string>();
        string[] lines = s.Split('\n');
        bool isInCommand = false;
        int hintStart = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            Match m = CommandPrefixRegex().Match(line);
            if (m.Success)
            {
                isInCommand = m.Groups[1].Value == command;
            }
            if (!isInCommand)
            {
                continue;
            }
            if (line == "{")
            {
                hintStart = commandLines.Count;
            }
            if (line == "}" && commandLines[commandLines.Count - 1].EndsWith(",", StringComparison.Ordinal))
            {
                commandLines[commandLines.Count - 1] =
                    commandLines[commandLines.Count - 1][..^1];
            }
            string trimmedLine = line.Trim();
            // Ignore position, already verified via caret.
            if (trimmedLine.StartsWith("\"position\": ", StringComparison.Ordinal))
            {
                continue;
            }
            if (trimmedLine.StartsWith("\"text\": ", StringComparison.Ordinal))
            {
                if (trimmedLine == "\"text\": \"\",")
                {
                    continue;
                }
                line = ReplaceFirst(line, "\"text\":", "\"label\":");
            }
            if (trimmedLine.StartsWith("\"kind\": ", StringComparison.Ordinal))
            {
                switch (trimmedLine)
                {
                    case "\"kind\": \"Parameter\",":
                        line = ReplaceFirst(line, "\"kind\": \"Parameter\",", "\"kind\": 2,");
                        break;
                    case "\"kind\": \"Type\",":
                        line = ReplaceFirst(line, "\"kind\": \"Type\",", "\"kind\": 1,");
                        break;
                    default:
                        continue;
                }
            }
            // Compare only text/value of display parts.
            // Record the presence of a span but not its details.
            if (trimmedLine.StartsWith("\"displayParts\": ", StringComparison.Ordinal))
            {
                var displayPartLines = new List<string>
                {
                    ReplaceFirst(line, "displayParts", "label"),
                };
                int j;
                for (j = i + 1; j < lines.Length; j++)
                {
                    string inner = lines[j];
                    string innerTrimmed = inner.Trim();
                    if (innerTrimmed.StartsWith("\"text\": ", StringComparison.Ordinal))
                    {
                        inner = ReplaceFirst(inner, "\"text\":", "\"value\":");
                    }
                    else if (innerTrimmed.StartsWith("\"span\": ", StringComparison.Ordinal))
                    {
                        displayPartLines.Add(ReplaceFirst(inner, "span", "location") + "},");
                        j += 3;
                        continue;
                    }
                    else if (innerTrimmed.StartsWith("\"file\": ", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (innerTrimmed == "]" || innerTrimmed == "],")
                    {
                        string fixedLine = inner;
                        if (innerTrimmed == "]")
                        {
                            fixedLine += ",";
                        }
                        displayPartLines.Add(fixedLine);
                        break;
                    }
                    displayPartLines.Add(inner);
                }
                // Add display parts at beginning of hint.
                commandLines.InsertRange(hintStart + 1, displayPartLines);
                i = j;
                continue;
            }

            commandLines.Add(ReplaceAll(line, new[]
            {
                ("\"whitespaceAfter\"", "\"paddingRight\""),
                ("\"whitespaceBefore\"", "\"paddingLeft\""),
            }));
        }
        return string.Join("\n", DropTrailingEmptyLines(commandLines));
    }

    // baselineutil.go:290-305 — the inlayHints DiffFixupNew body.
    private static string InlayHintsFixupNew(string s)
    {
        string[] lines = s.Split('\n');
        var fixedLines = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmedLine = line.Trim();
            if (trimmedLine.StartsWith("\"position\": ", StringComparison.Ordinal))
            {
                i += 3;
                continue;
            }
            if (trimmedLine.StartsWith("\"location\": ", StringComparison.Ordinal))
            {
                fixedLines.Add(line + "},");
                i += 12;
                continue;
            }
            fixedLines.Add(line);
        }
        return string.Join("\n", fixedLines);
    }

    // baselineutil.go:314-412 — the findAllReferences DiffFixupOld body.
    private static string FindAllReferencesFixupOld(string s)
    {
        const string findAllReferences = "findAllReferences";
        var commandLines = new List<string>();
        string[] lines = s.Split('\n');
        bool isInCommand = false;
        bool isInDetails = false;
        bool isInDefinitions = false;

        // Track file sections for sorting
        var fileSections = new List<(string FileName, List<string> Lines)>();
        string currentFileName = "";
        List<string> currentFileLines = [];

        void FlushSections()
        {
            fileSections.Sort(static (a, b) => string.CompareOrdinal(a.FileName, b.FileName));
            foreach ((_, List<string> sectionLines) in fileSections)
            {
                commandLines.AddRange(DropTrailingEmptyLines(sectionLines));
                commandLines.Add("");
            }
            fileSections = [];
        }

        foreach (string line in lines)
        {
            Match m = CommandPrefixRegex().Match(line);
            if (m.Success)
            {
                isInDetails = false;
                isInDefinitions = false;
                string commandName = m.Groups[1].Value;
                if (commandName == findAllReferences)
                {
                    isInCommand = true;
                    // Starting a new findAllReferences command block
                    if (currentFileName != "")
                    {
                        fileSections.Add((currentFileName, currentFileLines));
                    }
                    currentFileName = "";
                    currentFileLines = [];
                    FlushSections();
                    if (commandLines.Count > 0)
                    {
                        commandLines.Add("");
                        commandLines.Add("");
                    }
                    commandLines.Add(ReplaceAll(line, new[]
                    {
                        ("/tests/cases/fourslash", ""),
                        ("/server", ""),
                        ("<|", ""),
                        ("|>", ""),
                    }));
                    continue;
                }
                isInCommand = false;
            }
            if (!isInCommand)
            {
                continue;
            }
            if (line.Contains("// === Definitions ===", StringComparison.Ordinal) ||
                line.Contains("// === Details ===", StringComparison.Ordinal))
            {
                isInDefinitions = line.Contains("// === Definitions ===", StringComparison.Ordinal);
                isInDetails = line.Contains("// === Details ===", StringComparison.Ordinal);
                // Drop blank line before definitions/details
                if (currentFileLines.Count > 0 && currentFileLines[^1] == "")
                {
                    currentFileLines.RemoveAt(currentFileLines.Count - 1);
                }
            }
            // We don't diff the definitions or details sections
            if (!(isInDefinitions || isInDetails))
            {
                string fixedLine = ReplaceAll(line, new[]
                {
                    ("/tests/cases/fourslash", ""),
                    ("/server", ""),
                    ("<|", ""),
                    ("|>", ""),
                });
                fixedLine = ObjectRangeRegex().Replace(fixedLine, "");

                Match fileMatch = FilePrefixRegex().Match(fixedLine);
                if (fileMatch.Success)
                {
                    if (currentFileName != "")
                    {
                        fileSections.Add((currentFileName, currentFileLines));
                    }
                    currentFileName = fileMatch.Groups[1].Value;
                    currentFileLines = [fixedLine];
                }
                else
                {
                    currentFileLines.Add(fixedLine);
                }
            }
            else if (isInDetails && line == "  ]")
            {
                isInDetails = false;
            }
        }

        // Save any remaining file section
        if (currentFileName != "")
        {
            fileSections.Add((currentFileName, currentFileLines));
        }

        // Sort and add remaining file sections
        if (fileSections.Count > 0)
        {
            FlushSections();
        }

        return string.Join("\n", DropTrailingEmptyLines(commandLines));
    }

    private static string ReplaceFirst(string s, string oldValue, string newValue)
    {
        int index = s.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? s : s[..index] + newValue + s[(index + oldValue.Length)..];
    }
}
