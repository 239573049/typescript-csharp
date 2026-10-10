// Ported from tsgo internal/fourslash/fourslash.go — the code-action families:
//   code fixes      (:1587-2029)  → LS().ProvideCodeActions
//   organize imports(:2031-2084)  → LS().ProvideCodeActions with the organize kind
//   apply from completion (:2086-2165) → ProvideCompletion + ResolveCompletionItem
//   import fixes    (:2167-2397)  → ProvideCodeActions (quickfix arm)
//   file renames    (:4759-4968)  → GetEditsForFileRename
//   JSDoc completion (:1143-1206) → ProvideCompletion
//
// SHAPE SEAMS (recorded, not silent):
//   - Go sends the LSP `textDocument/codeAction` request through the test client; the C# port
//     calls the LS entry directly (D-012), building the same lsproto.CodeActionParams the Go
//     server would receive.
//   - Go's completion resolve request goes through the client; here it is the raw
//     ResolveCompletionItem(ctx, item, data) entry.
//   - Go's workspace/willRenameFiles request is the LSP file-operations request; the C# port
//     calls GetEditsForFileRename directly and applies the returned document changes.
//   - Post-edit on-type formatting has no C# formatting host, so typeText never produces
//     formatting edits here; the file-content assertions below still hold for the edit itself.

#if TS_FOURSLASH_RUNNER

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ts.Core;
using Ts.Ls;
using Xunit;
using LsProto = global::Ts.LsProto;

namespace Ts.TestUtil.Fourslash;

public sealed partial class FourslashTest
{
    // ---------------------------------------------------------------------
    // fourslash.go:1918-1981 — the quick-fix request plumbing.
    // ---------------------------------------------------------------------

    // fourslash.go:1919-1930 — getCodeFixActions: per-diagnostic fixes only (those with
    // diagnostics attached).
    private List<LsProto.CodeAction> getCodeFixActions(int[]? errorCodes = null)
    {
        return [.. getAllQuickFixActions(errorCodes).Where(static action => action.Diagnostics is { Length: > 0 })];
    }

    // fourslash.go:1933-1981 — getAllQuickFixActions, including the fix-all entries.
    private List<LsProto.CodeAction> getAllQuickFixActions(int[]? errorCodes = null)
    {
        LsProto.Diagnostic[] diagnostics = [.. getDiagnostics(activeFilename)];
        if (diagnostics.Length == 0)
        {
            return [];
        }

        LsProto.Diagnostic? diagnostic = SelectCodeFixDiagnostic(diagnostics, errorCodes is { Length: > 0 } ? errorCodes[0] : 0);
        if (diagnostic == null)
        {
            return [];
        }

        var parameters = new LsProto.CodeActionParams
        {
            TextDocument = new LsProto.TextDocumentIdentifier
            {
                Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(activeFilename),
            },
            Range = new LsProto.Range
            {
                Start = diagnostic.Range.Start,
                End = diagnostic.Range.End,
            },
            Context = new LsProto.CodeActionContext
            {
                Diagnostics = diagnostics,
            },
        };
        LsProto.CommandOrCodeActionArrayOrNull result = LS().ProvideCodeActions(parameters);

        var actions = new List<LsProto.CodeAction>();
        if (result.CommandOrCodeActionArray is { } items)
        {
            foreach (LsProto.CommandOrCodeAction item in items)
            {
                if (item.CodeAction is { } action && IsKind(action.Kind, LsProto.CodeActionKind.QuickFix))
                {
                    actions.Add(action);
                }
            }
        }
        return actions;
    }

    // fourslash.go:5808-5815 — selectCodeFixDiagnostic.
    private static LsProto.Diagnostic? SelectCodeFixDiagnostic(LsProto.Diagnostic[] diagnostics, int errorCode)
    {
        if (errorCode == 0)
        {
            return diagnostics[0];
        }
        return diagnostics.FirstOrDefault(diagnostic =>
            diagnostic.Code?.Integer != null && diagnostic.Code.Integer.Value == errorCode);
    }

    // The LSP CodeActionKind is a record struct over its string value in this port; the Go
    // comparisons are `*action.Kind == lsproto.CodeActionKindQuickFix`.
    private static bool IsKind(LsProto.CodeActionKind? kind, LsProto.CodeActionKind expected) =>
        kind is { } actual && string.Equals(actual.Value, expected.Value, StringComparison.Ordinal);

    // fourslash.go:1700-1715 — getCodeActionEditsForActiveFile.
    private List<LsProto.TextEdit> GetCodeActionEditsForActiveFile(LsProto.CodeAction action)
    {
        if (action.Edit?.Changes == null)
        {
            throw new InvalidOperationException("Code fix \"" + action.Title + "\" did not return text edits.");
        }
        if (action.Edit.Changes.Count != 1)
        {
            throw new InvalidOperationException(
                "Code fix \"" + action.Title + "\" returned edits for multiple files; rangeAfterCodeFix expects only the active file.");
        }
        if (action.Edit.Changes.TryGetValue(global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(activeFilename), out LsProto.TextEdit[]? edits))
        {
            return [.. edits];
        }
        throw new InvalidOperationException(
            "Code fix \"" + action.Title + "\" did not return edits for active file \"" + activeFilename + "\".");
    }

    // fourslash.go:1983-2013 — updateTextRangeForTextEdits.
    private TextRange UpdateTextRangeForTextEdits(TextRange textRange, IReadOnlyList<LsProto.TextEdit> edits)
    {
        FourslashScriptInfo script = getScriptInfo(activeFilename)!;
        var spans = new List<(int Pos, int End, int NewLength)>(edits.Count);
        foreach (LsProto.TextEdit edit in edits)
        {
            int pos = converters.LineAndCharacterToPosition(script, edit.Range.Start);
            int end = converters.LineAndCharacterToPosition(script, edit.Range.End);
            spans.Add((pos, end, edit.NewText.Length));
        }
        spans.Sort(static (a, b) => a.Pos.CompareTo(b.Pos));

        int posDelta = 0;
        int endDelta = 0;
        foreach ((int pos, int end, int newLength) in spans)
        {
            if (pos < textRange.Pos)
            {
                posDelta += newLength - (end - pos);
            }
            if (end <= textRange.End)
            {
                endDelta += newLength - (end - pos);
            }
        }
        return new TextRange(textRange.Pos + posDelta, textRange.End + endDelta);
    }

    // fourslash.go:2015-2029 — applyEditsToContent lives in Fourslash.cs (shared with VerifyCodeFix).

    // fourslash.go:5779-5787 — updatePositionForTextEdit has no C# caller: the only Go consumer
    // is the state baseline (statebaseline.go), which this port omits (see New()).

    // fourslash.go:5789-5798 — removeWhitespace.
    private static string RemoveWhitespace(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (global::Ts.StringUtil.StringUtil.IsWhiteSpaceLike(ch))
            {
                continue;
            }
            builder.Append(ch);
        }
        return builder.ToString();
    }

    // fourslash.go:5800-5806 — assertValidTextRange.
    private static void AssertValidTextRange(TextRange textRange, string message)
    {
        if (textRange.Pos >= 0 && textRange.End >= 0)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    // ---------------------------------------------------------------------
    // fourslash.go:1586-1662 — VerifyCodeFix.
    // ---------------------------------------------------------------------

    public void VerifyCodeFix(VerifyCodeFixOptions options)
    {
        UserPreferences? saved = ConfigureWithPreferences(options.UserPreferences);
        try
        {
            List<LsProto.CodeAction> actions = getCodeFixActions();

            if (actions.Count == 0)
            {
                throw new InvalidOperationException("No code fixes returned.");
            }
            if (options.Index >= actions.Count)
            {
                throw new InvalidOperationException(
                    "Code fix index " + options.Index + " out of range (got " + actions.Count + " fixes)");
            }

            LsProto.CodeAction matchingAction = actions[options.Index];
            if (matchingAction.Title != options.Description)
            {
                bool found = false;
                foreach (LsProto.CodeAction action in actions)
                {
                    if (action.Title == options.Description)
                    {
                        matchingAction = action;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    var titles = new List<string>();
                    foreach (LsProto.CodeAction action in actions)
                    {
                        titles.Add(action.Title);
                    }
                    throw new InvalidOperationException(
                        "No code fix with description \"" + options.Description + "\" at index " + options.Index
                        + " found. Available fixes: " + string.Join(", ", titles));
                }
            }

            string originalContent = getScriptInfo(activeFilename)!.Content;
            string expectedContent = options.NewFileContent;
            if (options.NewRangeContent.Length != 0)
            {
                TextRange selection = getSelection();
                if (selection.Pos == selection.End)
                {
                    List<RangeMarker> ranges = getRangesInFile(activeFilename);
                    if (ranges.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "Expected a selected range or fourslash range for NewRangeContent verification.");
                    }
                    selection = ranges[0].Range;
                }
                expectedContent = originalContent[..selection.Pos] + options.NewRangeContent + originalContent[selection.End..];
            }

            LsProto.WorkspaceEdit? edit = matchingAction.Edit;
            if (options.ApplyChanges)
            {
                ApplyCodeActionEdits(edit, apply: true);
                string actual = getScriptInfo(activeFilename)!.Content;
                Assert.Equal(expectedContent, actual);
            }
            else
            {
                string actual = getScriptInfo(activeFilename)!.Content;
                if (edit?.Changes != null)
                {
                    LsProto.DocumentUri expectedUri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(activeFilename);
                    // map-iter: 序无关 — every change set is applied to the same content snapshot,
                    // and a file other than the active one is an immediate failure.
                    foreach (KeyValuePair<LsProto.DocumentUri, LsProto.TextEdit[]> entry in edit.Changes)
                    {
                        if (entry.Key.Value != expectedUri.Value)
                        {
                            throw new InvalidOperationException(
                                "Code fix returned edits for unexpected URI " + entry.Key.Value
                                + " (expected " + expectedUri.Value + ")");
                        }
                        actual = applyEditsToContent(actual, entry.Value);
                    }
                }
                Assert.Equal(expectedContent, actual);
            }
        }
        finally
        {
            RestorePreferences(saved);
        }
    }

    // The shared `for uri, edits := range *edit.Changes { check uri; f.applyTextEdits(t, edits) }`
    // body of VerifyCodeFix / VerifyCodeFixAll.
    private void ApplyCodeActionEdits(LsProto.WorkspaceEdit? edit, bool apply)
    {
        if (edit?.Changes == null)
        {
            return;
        }
        LsProto.DocumentUri expectedUri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(activeFilename);
        // map-iter: 序无关 — see VerifyCodeFix's non-applying branch.
        foreach (KeyValuePair<LsProto.DocumentUri, LsProto.TextEdit[]> entry in edit.Changes)
        {
            if (entry.Key.Value != expectedUri.Value)
            {
                throw new InvalidOperationException(
                    "Code fix returned edits for unexpected URI " + entry.Key.Value
                    + " (expected " + expectedUri.Value + ")");
            }
            if (apply)
            {
                applyTextEdits(entry.Value);
            }
        }
    }

    // ---------------------------------------------------------------------
    // fourslash.go:1664-1698 — VerifyRangeAfterCodeFix.
    // ---------------------------------------------------------------------

    public void VerifyRangeAfterCodeFix(string expectedText, bool includeWhitespace, int errorCode, int index)
    {
        List<LsProto.CodeAction> actions = getCodeFixActions([errorCode]);
        if (actions.Count == 0)
        {
            throw new InvalidOperationException("No code fixes returned.");
        }
        if (index >= actions.Count)
        {
            throw new InvalidOperationException(
                "Code fix index " + index + " out of range (got " + actions.Count + " fixes)");
        }

        LsProto.CodeAction action = actions[index];
        List<RangeMarker> ranges = getRangesInFile(activeFilename);
        if (ranges.Count != 1)
        {
            throw new InvalidOperationException(
                "Expected exactly one range in \"" + activeFilename + "\", got " + ranges.Count + ".");
        }

        List<LsProto.TextEdit> edits = GetCodeActionEditsForActiveFile(action);
        TextRange updatedRange = UpdateTextRangeForTextEdits(ranges[0].Range, edits);
        AssertValidTextRange(
            updatedRange,
            "Code fix \"" + action.Title + "\" replaced part of the expected range; unable to compute rangeAfterCodeFix result.");

        applyTextEdits(edits);
        string actualContent = getScriptInfo(activeFilename)!.Content;
        string actualText = actualContent[updatedRange.Pos..updatedRange.End];

        if (includeWhitespace)
        {
            Assert.Equal(expectedText, actualText);
            return;
        }
        Assert.Equal(RemoveWhitespace(expectedText), RemoveWhitespace(actualText));
    }

    // ---------------------------------------------------------------------
    // fourslash.go:1717-1809 — VerifyCodeFixAvailable / NotAvailable / AvailableExact.
    // ---------------------------------------------------------------------

    public void VerifyCodeFixAvailable(string[]? expectedDescriptions)
    {
        List<LsProto.CodeAction> actions = getCodeFixActions();

        if (expectedDescriptions == null)
        {
            if (actions.Count == 0)
            {
                throw new InvalidOperationException("Expected code fixes to be available, but got none.");
            }
            return;
        }

        if (expectedDescriptions.Length == 0)
        {
            VerifyCodeFixNotAvailable();
            return;
        }

        foreach (string expected in expectedDescriptions)
        {
            if (!actions.Exists(action => action.Title == expected))
            {
                throw new InvalidOperationException(
                    "Expected code fix with description \"" + expected + "\" not found. Available fixes: "
                    + string.Join(", ", actions.Select(static a => a.Title)));
            }
        }
    }

    public void VerifyCodeFixNotAvailable(params string[] expected)
    {
        List<LsProto.CodeAction> actions = getCodeFixActions();
        if (expected.Length == 0)
        {
            if (actions.Count == 0)
            {
                return;
            }
            throw new InvalidOperationException(
                "Expected no code fixes, but got: " + string.Join(", ", actions.Select(static a => a.Title)));
        }
        foreach (string title in expected)
        {
            foreach (LsProto.CodeAction action in actions)
            {
                if (action.Title == title)
                {
                    throw new InvalidOperationException(
                        "Expected code fix with description \"" + title + "\" not to be available.");
                }
            }
        }
    }

    public void VerifyCodeFixAvailableExact(string[] expectedDescriptions)
    {
        List<LsProto.CodeAction> actions = getCodeFixActions();
        if (actions.Count != expectedDescriptions.Length)
        {
            throw new InvalidOperationException(
                "Expected exactly " + expectedDescriptions.Length + " code fixes, but got " + actions.Count
                + ". Available fixes: " + string.Join(", ", actions.Select(static a => a.Title)));
        }
        foreach (string expected in expectedDescriptions)
        {
            if (!actions.Exists(action => action.Title == expected))
            {
                throw new InvalidOperationException(
                    "Expected code fix with description \"" + expected + "\" not found. Available fixes: "
                    + string.Join(", ", actions.Select(static a => a.Title)));
            }
        }
    }

    // ---------------------------------------------------------------------
    // fourslash.go:1811-1865 — VerifyCodeFixAll.
    // ---------------------------------------------------------------------

    public void VerifyCodeFixAll(VerifyCodeFixAllOptions options)
    {
        List<LsProto.CodeAction> actions = getAllQuickFixActions();
        if (actions.Count == 0)
        {
            throw new InvalidOperationException("No code fixes available for fixId \"" + options.FixID + "\"");
        }

        // Fix-all entries are the quickfix actions with no diagnostics attached.
        List<LsProto.CodeAction> fixAllCandidates = [.. actions.Where(static action => action.Diagnostics is not { Length: > 0 })];

        LsProto.CodeAction? fixAllAction = null;
        if (fixAllCandidates.Count == 1)
        {
            fixAllAction = fixAllCandidates[0];
        }
        else
        {
            foreach (LsProto.CodeAction action in fixAllCandidates)
            {
                if (action.Title.Contains(options.FixID, StringComparison.OrdinalIgnoreCase))
                {
                    fixAllAction = action;
                    break;
                }
            }
        }

        if (fixAllAction == null)
        {
            throw new InvalidOperationException(
                "No fix-all code action found for fixId \"" + options.FixID + "\". Available fixes: "
                + string.Join(", ", actions.Select(static a => a.Title)));
        }

        ApplyCodeActionEdits(fixAllAction.Edit, apply: true);

        string actual = getScriptInfo(activeFilename)!.Content;
        Assert.Equal(options.NewFileContent, actual);
    }

    // ---------------------------------------------------------------------
    // fourslash.go:2031-2084 — VerifyOrganizeImports.
    // ---------------------------------------------------------------------

    public void VerifyOrganizeImports(string expectedContent, LsProto.CodeActionKind codeActionKind, UserPreferences? preferences)
    {
        UserPreferences? saved = ConfigureWithPreferences(preferences);
        try
        {
            FourslashScriptInfo script = getScriptInfo(activeFilename)!;
            var parameters = new LsProto.CodeActionParams
            {
                TextDocument = new LsProto.TextDocumentIdentifier
                {
                    Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(activeFilename),
                },
                Range = new LsProto.Range
                {
                    Start = new LsProto.Position { Line = 0, Character = 0 },
                    End = converters.PositionToLineAndCharacter(script, script.Content.Length),
                },
                Context = new LsProto.CodeActionContext
                {
                    Diagnostics = [],
                    Only = [codeActionKind],
                },
            };
            LsProto.CommandOrCodeActionArrayOrNull result = LS().ProvideCodeActions(parameters);

            if (result.CommandOrCodeActionArray is not { Length: > 0 } items)
            {
                throw new InvalidOperationException("No organize imports code action found");
            }

            LsProto.CodeAction? organizeAction = null;
            foreach (LsProto.CommandOrCodeAction item in items)
            {
                if (item.CodeAction is { } candidate && IsKind(candidate.Kind, codeActionKind))
                {
                    organizeAction = candidate;
                    break;
                }
            }
            if (organizeAction == null)
            {
                throw new InvalidOperationException("No organize imports code action found");
            }

            ApplyCodeActionEdits(organizeAction.Edit, apply: true);

            string actualContent = getScriptInfo(activeFilename)!.Content;
            if (actualContent != expectedContent)
            {
                throw new InvalidOperationException(
                    "Organize imports result doesn't match.\nExpected:\n" + expectedContent + "\n\nActual:\n" + actualContent);
            }
        }
        finally
        {
            RestorePreferences(saved);
        }
    }

    // ---------------------------------------------------------------------
    // fourslash.go:2086-2165 — VerifyApplyCodeActionFromCompletion.
    // ---------------------------------------------------------------------

    public void VerifyApplyCodeActionFromCompletion(string? markerName, ApplyCodeActionFromCompletionOptions options)
    {
        if (markerName != null)
        {
            GoToMarker(markerName);
        }

        UserPreferences userPreferencesForAction = options.UserPreferences ?? UserPreferences.NewDefaultUserPreferences();

        UserPreferences saved = userPreferences.Clone();
        Configure(userPreferencesForAction);
        try
        {
            // Already configured, so the preference override is not passed in again. The raw
            // completions entry returns the wire items wrapped with their IncludeSymbols payload
            // (Ts.Ls.CompletionItem.Item); only the wire item is comparable to options here.
            Ts.Ls.CompletionList? completionsList = getCompletions();
            List<LsProto.CompletionItem> items = [.. (completionsList?.Items ?? []).Select(static wrapper => wrapper.Item!).Where(item =>
            {
                if (item.Label != options.Name || item.Data == null)
                {
                    return false;
                }
                LsProto.CompletionItemData data = item.Data;
                if (options.AutoImportFix != null)
                {
                    return data.AutoImport != null
                        && (options.AutoImportFix.ModuleSpecifier.Length == 0
                            || data.AutoImport.ModuleSpecifier == options.AutoImportFix.ModuleSpecifier);
                }
                if (data.AutoImport == null && data.Source.Length != 0 && data.Source == options.Source)
                {
                    return true;
                }
                return data.AutoImport != null && data.AutoImport.ModuleSpecifier == options.Source;
            })];

            if (items.Count == 0)
            {
                throw new InvalidOperationException(
                    "Code action '" + options.Name + "' from source '" + options.Source + "' not found in completions.");
            }

            LsProto.CompletionItem? correctItem = null;
            LsProto.CompletionItem correctResolvedItem = new();
            foreach (LsProto.CompletionItem item in items)
            {
                correctResolvedItem = ResolveCompletionItem(item);
                string actualDetail = correctResolvedItem.Detail ?? "";
                if (actualDetail.Contains(options.Description, StringComparison.Ordinal)
                    && correctResolvedItem.AdditionalTextEdits != null)
                {
                    correctItem = item;
                    break;
                }
            }

            if (correctItem == null)
            {
                throw new InvalidOperationException(
                    "No matching code action found for '" + options.Name + "' from source '" + options.Source + "'.");
            }

            // apply the item to the test files
            applyTextEdits(correctResolvedItem.AdditionalTextEdits!);
            if (options.NewFileContent != null)
            {
                Assert.Equal(options.NewFileContent, getScriptInfo(activeFilename)!.Content);
            }
            else if (options.NewRangeContent != null)
            {
                throw new NotSupportedException("!!! TODO (fourslash.go:2162-2164 NewRangeContent)");
            }
        }
        finally
        {
            RestorePreferences(saved);
        }
    }

    // ---------------------------------------------------------------------
    // fourslash.go:2167-2288 — VerifyImportFixAtPosition.
    // ---------------------------------------------------------------------

    public void VerifyImportFixAtPosition(string[] expectedTexts, UserPreferences? preferences)
    {
        string fileName = activeFilename;
        List<RangeMarker> filteredRanges = [.. Ranges().Where(r => r.FileName == fileName)];
        if (filteredRanges.Count > 1)
        {
            throw new InvalidOperationException("Exactly one range should be specified in the testfile.");
        }
        RangeMarker? rangeMarker = filteredRanges.Count == 1 ? filteredRanges[0] : null;

        UserPreferences? saved = ConfigureWithPreferences(preferences);
        try
        {
            LsProto.Diagnostic[] diagnostics = [.. getDiagnostics(activeFilename)];

            var parameters = new LsProto.CodeActionParams
            {
                TextDocument = new LsProto.TextDocumentIdentifier
                {
                    Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(activeFilename),
                },
                Range = new LsProto.Range
                {
                    Start = currentCaretPosition,
                    End = currentCaretPosition,
                },
                Context = new LsProto.CodeActionContext
                {
                    Diagnostics = diagnostics,
                },
            };
            LsProto.CommandOrCodeActionArrayOrNull result = LS().ProvideCodeActions(parameters);

            List<LsProto.CodeAction> importActions = [];
            if (result.CommandOrCodeActionArray is { } items)
            {
                foreach (LsProto.CommandOrCodeAction item in items)
                {
                    if (item.CodeAction is { } action
                        && IsKind(action.Kind, LsProto.CodeActionKind.QuickFix)
                        && action.Diagnostics is { Length: > 0 })
                    {
                        importActions.Add(action);
                    }
                }
            }

            if (importActions.Count == 0)
            {
                if (expectedTexts.Length != 0)
                {
                    throw new InvalidOperationException("No codefixes returned.");
                }
                return;
            }

            FourslashScriptInfo script = getScriptInfo(activeFilename)!;
            string originalContent = script.Content;
            LsProto.Position savedCaret = currentCaretPosition;
            var actualTextArray = new List<string>(importActions.Count);
            foreach (LsProto.CodeAction action in importActions)
            {
                if (action.Edit?.Changes != null)
                {
                    if (action.Edit.Changes.Count != 1)
                    {
                        throw new InvalidOperationException("Expected exactly 1 change, got " + action.Edit.Changes.Count);
                    }
                    // map-iter: 序无关 — a single change set, and any non-active file is fatal.
                    foreach (KeyValuePair<LsProto.DocumentUri, LsProto.TextEdit[]> entry in action.Edit.Changes)
                    {
                        if (entry.Key.Value != global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(activeFilename).Value)
                        {
                            throw new InvalidOperationException(
                                "Expected change to file " + activeFilename + ", got " + entry.Key.Value);
                        }
                        applyTextEdits(entry.Value);
                    }
                }

                actualTextArray.Add(rangeMarker != null
                    ? getRangeText(rangeMarker)
                    : getScriptInfo(activeFilename)!.Content);

                // Restore the original content for the next fix.
                editScriptAndUpdateMarkers(activeFilename, 0, getScriptInfo(activeFilename)!.Content.Length, originalContent);
                currentCaretPosition = savedCaret;
            }

            if (expectedTexts.Length != actualTextArray.Count)
            {
                throw new InvalidOperationException(
                    "Expected " + expectedTexts.Length + " import fixes, got " + actualTextArray.Count + ":\n\n"
                    + string.Join("\n\n" + new string('-', 20) + "\n\n", actualTextArray));
            }
            for (int i = 0; i < expectedTexts.Length; i++)
            {
                Assert.Equal(expectedTexts[i], actualTextArray[i]);
            }
        }
        finally
        {
            RestorePreferences(saved);
        }
    }

    // ---------------------------------------------------------------------
    // fourslash.go:2290-2397 — VerifyImportFixModuleSpecifiers / extractModuleSpecifier.
    // ---------------------------------------------------------------------

    public void VerifyImportFixModuleSpecifiers(string markerName, string[] expectedModuleSpecifiers, UserPreferences? preferences)
    {
        GoToMarker(markerName);

        UserPreferences? saved = ConfigureWithPreferences(preferences);
        try
        {
            LsProto.Diagnostic[] diagnostics = [.. getDiagnostics(activeFilename)];

            var parameters = new LsProto.CodeActionParams
            {
                TextDocument = new LsProto.TextDocumentIdentifier
                {
                    Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(activeFilename),
                },
                Range = new LsProto.Range
                {
                    Start = currentCaretPosition,
                    End = currentCaretPosition,
                },
                Context = new LsProto.CodeActionContext
                {
                    Diagnostics = diagnostics,
                },
            };
            LsProto.CommandOrCodeActionArrayOrNull result = LS().ProvideCodeActions(parameters);

            var actualModuleSpecifiers = new List<string>();
            if (result.CommandOrCodeActionArray is { } items)
            {
                foreach (LsProto.CommandOrCodeAction item in items)
                {
                    if (item.CodeAction is { } action
                        && IsKind(action.Kind, LsProto.CodeActionKind.QuickFix)
                        && action.Edit?.Changes != null)
                    {
                        // map-iter: 序无关 — the specifiers are accumulated in first-seen order and
                        // each change set is independent.
                        foreach (LsProto.TextEdit[] changeEdits in action.Edit.Changes.Values)
                        {
                            foreach (LsProto.TextEdit edit in changeEdits)
                            {
                                string moduleSpec = ExtractModuleSpecifier(edit.NewText);
                                if (moduleSpec.Length != 0 && !actualModuleSpecifiers.Contains(moduleSpec))
                                {
                                    actualModuleSpecifiers.Add(moduleSpec);
                                }
                            }
                        }
                    }
                }
            }

            if (actualModuleSpecifiers.Count != expectedModuleSpecifiers.Length)
            {
                throw new InvalidOperationException(
                    "Expected " + expectedModuleSpecifiers.Length + " module specifiers, got " + actualModuleSpecifiers.Count
                    + ".\nExpected: [" + string.Join(", ", expectedModuleSpecifiers) + "]"
                    + "\nActual: [" + string.Join(", ", actualModuleSpecifiers) + "]");
            }
            for (int i = 0; i < expectedModuleSpecifiers.Length; i++)
            {
                if (actualModuleSpecifiers[i] != expectedModuleSpecifiers[i])
                {
                    throw new InvalidOperationException(
                        "Module specifier mismatch at index " + i + ".\nExpected: ["
                        + string.Join(", ", expectedModuleSpecifiers) + "]\nActual: ["
                        + string.Join(", ", actualModuleSpecifiers) + "]");
                }
            }
        }
        finally
        {
            RestorePreferences(saved);
        }
    }

    // fourslash.go:2367-2397 — extractModuleSpecifier.
    private static string ExtractModuleSpecifier(string text)
    {
        int idx = text.IndexOf("from \"", StringComparison.Ordinal);
        if (idx != -1)
        {
            int start = idx + 6; // len("from \"")
            int end = text.IndexOf('"', start);
            if (end != -1)
            {
                return text[start..end];
            }
        }
        idx = text.IndexOf("from '", StringComparison.Ordinal);
        if (idx != -1)
        {
            int start = idx + 6; // len("from '")
            int end = text.IndexOf('\'', start);
            if (end != -1)
            {
                return text[start..end];
            }
        }
        idx = text.IndexOf("require(\"", StringComparison.Ordinal);
        if (idx != -1)
        {
            int start = idx + 9; // len("require(\"")
            int end = text.IndexOf('"', start);
            if (end != -1)
            {
                return text[start..end];
            }
        }
        idx = text.IndexOf("require('", StringComparison.Ordinal);
        if (idx != -1)
        {
            int start = idx + 9; // len("require('")
            int end = text.IndexOf('\'', start);
            if (end != -1)
            {
                return text[start..end];
            }
        }
        return "";
    }

    // ---------------------------------------------------------------------
    // fourslash.go:4759-4968 — the file-rename family.
    // ---------------------------------------------------------------------

    // fourslash.go:4843-4861 — VerifyWillRenameFilesEdits. Go sends workspace/willRenameFiles;
    // the C# port calls the file-rename engine directly (see the header).
    public void VerifyWillRenameFilesEdits(string oldPath, string newPath, IReadOnlyDictionary<string, string> expectedFileContents, UserPreferences? preferences)
    {
        UserPreferences? saved = ConfigureWithPreferences(preferences);
        try
        {
            willRenameFilesWorker(oldPath, newPath);

            // map-iter: 序无关 — independent per-file assertions.
            foreach (KeyValuePair<string, string> entry in expectedFileContents)
            {
                FourslashScriptInfo? script = getOrLoadScriptInfo(entry.Key);
                if (script == null)
                {
                    throw new InvalidOperationException("Expected script info for " + entry.Key + ", but got nil");
                }
                Assert.Equal(entry.Value, script.Content);
            }
        }
        finally
        {
            RestorePreferences(saved);
        }
    }

    // fourslash.go:4766-4828 — willRenameFilesWorker.
    private void willRenameFilesWorker(params string[] oldPathsAndNewPaths)
    {
        // The C# call shape pairs each old path with the new one that follows it.
        for (int i = 0; i + 1 < oldPathsAndNewPaths.Length; i += 2)
        {
            string oldPath = oldPathsAndNewPaths[i];
            string newPath = oldPathsAndNewPaths[i + 1];
            List<LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile> edits = LS().GetEditsForFileRename(
                cancellationToken,
                global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(oldPath),
                global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(newPath));
            ApplyFileRenameDocumentChanges(edits);
            RenameFileOrDirectory(oldPath, newPath);
        }
    }

    // fourslash.go:4770-4812 — the DocumentChanges arm applied to the harness's script infos.
    private void ApplyFileRenameDocumentChanges(List<LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile> documentChanges)
    {
        foreach (LsProto.TextDocumentEditOrCreateFileOrRenameFileOrDeleteFile docChange in documentChanges)
        {
            if (docChange.TextDocumentEdit is { } textDocumentEdit)
            {
                string fileName = textDocumentEdit.TextDocument.Uri.FileName();
                FourslashScriptInfo? script = getOrLoadScriptInfo(fileName);
                if (script == null)
                {
                    continue;
                }
                var changes = new List<TextChange>(textDocumentEdit.Edits.Length);
                foreach (LsProto.TextEditOrAnnotatedTextEditOrSnippetTextEdit edit in textDocumentEdit.Edits)
                {
                    LsProto.TextEdit? textEdit = edit.TextEdit;
                    if (textEdit == null)
                    {
                        continue;
                    }
                    changes.Add(new TextChange(converters.FromLSPRange(script, textEdit.Range), textEdit.NewText));
                }
                editScriptAndUpdateMarkersWorker(fileName, changes);
            }
        }
    }

    // fourslash.go:4863-4874 — getPathUpdater.
    private Func<string, (string? NewPath, bool Updated)> GetPathUpdater(string oldPath, string newPath)
    {
        return path =>
        {
            var compareOptions = new global::Ts.TsPath.ComparePathsOptions
            {
                UseCaseSensitiveFileNames = vfs.UseCaseSensitiveFileNames(),
            };
            if (global::Ts.TsPath.TsPath.ComparePaths(path, oldPath, compareOptions) == 0)
            {
                return (newPath, true);
            }
            if (global::Ts.TsPath.TsPath.StartsWithDirectory(path, oldPath, vfs.UseCaseSensitiveFileNames()))
            {
                return (newPath + path[oldPath.Length..], true);
            }
            return (null, false);
        };
    }

    // fourslash.go:4876-4968 — renameFileOrDirectory. The VFS write/remove and the file-watch
    // notifications have no observable effect on the direct-transport harness (the LS reads the
    // script infos and the ProjectService, not the VFS), so the script-info rekeying, the
    // active-filename update and the open-file set are what this port performs.
    private void RenameFileOrDirectory(string oldPath, string newPath)
    {
        Func<string, (string? NewPath, bool Updated)> pathUpdater = GetPathUpdater(oldPath, newPath);

        var oldFileNames = new List<string>();
        if (vfs.TryReadFile(oldPath, out string? _))
        {
            oldFileNames.Add(oldPath);
        }
        else
        {
            foreach (string path in EnumerateVfsFiles())
            {
                if (path.StartsWith(oldPath.TrimEnd('/') + "/", StringComparison.Ordinal))
                {
                    oldFileNames.Add(path);
                }
            }
        }
        if (oldFileNames.Count == 0)
        {
            throw new InvalidOperationException("rename source " + oldPath + " did not exist in test environment");
        }

        foreach (string oldFileName in oldFileNames)
        {
            (string? newFileName, bool updated) = pathUpdater(oldFileName);
            if (!updated || newFileName == null)
            {
                throw new InvalidOperationException("failed to compute renamed path for " + oldFileName);
            }

            bool wasOpen = openFiles.Remove(oldFileName);
            if (wasOpen)
            {
                projectService.CloseDocument(oldFileName);
            }

            if (scriptInfos.TryGetValue(oldFileName, out FourslashScriptInfo? script))
            {
                scriptInfos.Remove(oldFileName);
                scriptInfos[newFileName] = FourslashScriptInfo.New(newFileName, script.Content);
            }

            if (wasOpen)
            {
                projectService.OpenDocument(newFileName, getScriptInfo(newFileName)!.Content, getScriptInfo(newFileName)!.Version);
                openFiles.Add(newFileName);
            }
        }

        (string? updatedActive, bool activeUpdated) = pathUpdater(activeFilename);
        if (activeUpdated && updatedActive != null)
        {
            activeFilename = updatedActive;
        }
    }

    // ---------------------------------------------------------------------
    // fourslash.go:1143-1218 — the JSDoc completion family.
    // ---------------------------------------------------------------------

    // fourslash.go:1143-1218 — the JSDoc completion family. Go's markerInput is MarkerInput
    // (string | *Marker); the corpus reaches the loop form with either.
    public void VerifyJSDocCompletion(object? markerInput, int expectedOffset, string expectedText, bool? generateReturnInDocTemplate)
    {
        goToMarkerInput(markerInput);

        // fourslash.go:1148-1153 — the JSDoc preferences ride the request (getCompletions takes
        // a *UserPreferences); the client's preference set is the code path the raw LS entry
        // reads, so the override is installed for the duration of the verification.
        UserPreferences? saved = null;
        if (generateReturnInDocTemplate != null)
        {
            UserPreferences preferences = UserPreferences.NewDefaultUserPreferences();
            preferences.GenerateReturnInDocTemplate = TristateExtensions.BoolToTristate(generateReturnInDocTemplate.Value);
            saved = ConfigureWithPreferences(preferences);
        }

        try
        {
            Ts.Ls.CompletionList? list = getCompletions();
            LsProto.CompletionItem? item = FindJSDocCompletionItem(list);
            if (item == null)
            {
                FourslashScriptInfo script = getScriptInfo(activeFilename)!;
                int insertStart = converters.LineAndCharacterToPosition(script, currentCaretPosition);
                Insert("/**");
                list = getCompletions();
                item = FindJSDocCompletionItem(list);
                editScriptAndUpdateMarkers(activeFilename, insertStart, insertStart + 3, "");
                currentCaretPosition = converters.PositionToLineAndCharacter(script, insertStart);
            }
            if (list == null)
            {
                throw new InvalidOperationException(
                    getCurrentPositionPrefix() + "Expected JSDoc completion, got nil completion list.");
            }
            if (item == null)
            {
                throw new InvalidOperationException(
                    getCurrentPositionPrefix() + "Expected JSDoc completion item, got none.");
            }
            if (item.TextEdit?.InsertReplaceEdit == null)
            {
                throw new InvalidOperationException(
                    getCurrentPositionPrefix() + "Expected JSDoc completion to have insert/replace edit.");
            }
            // assert.Equal(t, item.TextEdit.InsertReplaceEdit.NewText, expectedText) — testify's
            // (actual, expected) argument order, i.e. expected first here.
            Assert.Equal(expectedText, item.TextEdit.InsertReplaceEdit.NewText);
            _ = expectedOffset; // The completion path uses snippet placeholders for caret placement.
        }
        finally
        {
            RestorePreferences(saved);
        }
    }

    public void VerifyNoJSDocCompletion(object? markerInput)
    {
        goToMarkerInput(markerInput);

        Ts.Ls.CompletionList? list = getCompletions();
        if (FindJSDocCompletionItem(list) != null)
        {
            throw new InvalidOperationException(getCurrentPositionPrefix() + "Did not expect JSDoc completion item.");
        }

        FourslashScriptInfo script = getScriptInfo(activeFilename)!;
        int insertStart = converters.LineAndCharacterToPosition(script, currentCaretPosition);
        Insert("/**");
        list = getCompletions();
        LsProto.CompletionItem? item = FindJSDocCompletionItem(list);
        editScriptAndUpdateMarkers(activeFilename, insertStart, insertStart + 3, "");
        currentCaretPosition = converters.PositionToLineAndCharacter(script, insertStart);
        if (item != null)
        {
            throw new InvalidOperationException(getCurrentPositionPrefix() + "Did not expect JSDoc completion item.");
        }
    }

    // fourslash.go:1199-1206 — findJSDocCompletionItem. Go walks the wire items; the C# raw entry
    // wraps each wire item with the IncludeSymbols payload (includeSymbols=false keeps Item set).
    private static LsProto.CompletionItem? FindJSDocCompletionItem(Ts.Ls.CompletionList? list) =>
        list?.Items?.FirstOrDefault(static wrapper => wrapper.Item?.Label == "/** */")?.Item;
}

#endif

