// Ported from tsgo internal/fourslash/baselineutil.go (:23-97, :493-501) — the baseline-command
// registry: the command name constants, the section header writer (addResultToBaseline), the
// baseline file-name/extension mapping, and normalizeCommandName. The per-feature baseline
// *producers* stay with the session partials that own the LS requests; everything without an
// LS dependency lives here so Ts.TestUtil keeps its dependency boundary.
//
// PORT-NOTE: Go's verifyBaselines flushes each accumulated command through baseline.Run with
// subfolder "fourslash/<normalizeCommandName(command)>". The submodule branches of
// getBaselineOptions (DiffFixupOld fixups for Strada parity) are carried over verbatim-shaped
// but inert until the C# repo grows a tests/gen corpus (isSubmoduleTest only fires for those
// paths); the call sites keep Go's structure.

using System;
using System.Collections.Generic;
using System.Text;

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

    // Go's named constant for the rename diff fixup command match (baselineutil.go:152).
    internal bool Matches(string commandName) => string.Equals(Value, commandName, StringComparison.Ordinal);

    public override string ToString() => Value;
}

// Per-session accumulator for the baselines map (Go: f.baselines map[baselineCommand]*strings.Builder).
public sealed class FourslashBaselines
{
    private readonly Dictionary<FourslashBaselineCommand, StringBuilder> builders = new();

    public int Count => builders.Count;

    // baselineutil.go:47-65 — addResultToBaseline (minus the state-baseline branch, which the
    // session owns because it reroutes to its single state builder).
    public void AddResult(FourslashBaselineCommand command, string actual)
    {
        StringBuilder b = GetOrAdd(command);
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

    public StringBuilder GetOrAdd(FourslashBaselineCommand command)
    {
        if (!builders.TryGetValue(command, out StringBuilder? b))
        {
            b = new StringBuilder();
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

public static class FourslashBaselineUtil
{
    // baselineutil.go:76-78 — getBaselineFileName.
    public static string GetBaselineFileName(string testBaseName, FourslashBaselineCommand command) =>
        testBaseName + "." + GetBaselineExtension(command);

    // baselineutil.go:80-93 — getBaselineExtension.
    public static string GetBaselineExtension(FourslashBaselineCommand command)
    {
        if (command.Value == FourslashBaselineCommand.QuickInfo.Value ||
            command.Value == FourslashBaselineCommand.SignatureHelp.Value ||
            command.Value == FourslashBaselineCommand.SmartSelection.Value ||
            command.Value == FourslashBaselineCommand.InlayHints.Value ||
            command.Value == FourslashBaselineCommand.NonSuggestionDiagnostics.Value ||
            command.Value == FourslashBaselineCommand.DocumentSymbols.Value ||
            command.Value == FourslashBaselineCommand.ClosingTag.Value ||
            command.Value == FourslashBaselineCommand.VsFindAllReferences.Value)
        {
            return "baseline";
        }
        if (command.Matches(FourslashBaselineCommand.CallHierarchy.Value))
        {
            return "callHierarchy.txt";
        }
        if (command.Matches(FourslashBaselineCommand.AutoImports.Value))
        {
            return "baseline.md";
        }
        if (command.Matches(FourslashBaselineCommand.LinkedEditing.Value))
        {
            return "linkedEditing.txt";
        }
        return "baseline.jsonc";
    }

    // baselineutil.go:493-495 — isSubmoduleTest. The C# harness has no tests/gen corpus yet,
    // so this only ever returns false today; carried verbatim for the day it lands.
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

    // baselineutil.go:95-491 — getBaselineOptions. The non-submodule branch is the only one
    // reachable today; the submodule fixups (callHierarchy/rename/inlayHints DiffFixupOld) are
    // ported in the session partial that needs them, keeping this file LS-free.
    public static Baseline.Options GetBaselineOptions(FourslashBaselineCommand command, string testPath)
    {
        string subfolder = "fourslash/" + NormalizeCommandName(command.Value);
        if (!IsSubmoduleTest(testPath))
        {
            return new Baseline.Options { Subfolder = subfolder };
        }

        return command.Value switch
        {
            "Smart Selection" => new Baseline.Options { Subfolder = subfolder, IsSubmodule = true },
            // The callHierarchy/rename/inlayHints DiffFixupOld bodies (baselineutil.go:108-491)
            // are Strada-parity fixups over baseline text shapes the C# producers do not emit
            // yet; when their producers land they must bring their fixup with them.
            _ => new Baseline.Options { Subfolder = subfolder, IsSubmodule = true },
        };
    }
}
