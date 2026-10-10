// Ported from tsgo internal/fourslash/fourslash.go — the verify families that have a real C#
// LS counterpart, in the Go file's section order:
//   quickInfo      (:3977-4048)  → GetQuickInfoAtPosition (foundation QuickInfo record)
//   completions    (:1011-1560)  → GetCompletionsAtPosition / ResolveCompletionItem
//   rename         (:4575-4841)  → GetRenameInfo / ProvideRename (WorkspaceEdit.Changes arm)
//   diagnostics    (:5218-5272, :5632-5777) → ProvideDiagnostics
//   signature help (:4148-4451)  → GetSignatureHelpItems (foundation record, reduced shape)
//   linkedEditing  (:5191-5216)  → ProvideLinkedEditingRange
// plus internal/fourslash/semantictokens.go (whole file) → ProvideSemanticTokens.
//
// SHAPE SEAMS (recorded, not silent):
//   - Hover: Go asserts the full markdown string "```typescript\n<text>\n```\n<docs>"; the C#
//     foundation returns DisplayString + Documentation fields, so the assert compares the
//     fields. Documentation text is the foundation's plain-text rendering.
//   - Signature help: Go compares lsproto.SignatureHelp with label-offset parameters; the C#
//     foundation record carries Label strings and an IsVariadic flag, so the expected model is
//     the reduced ExpectedSignatureInformation below.
//   - VerifyCompletions' AndApplyCodeAction/AndHasNoCodeAction continuations ride the
//     auto-import registry, which the direct-program host does not provide (null); they are
//     omitted and VerifyCompletions returns the raw list instead of the closures struct.

#if TS_FOURSLASH_RUNNER

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ts.Core;
using Ts.Ls;
using Ts.Ls.LsConv;
using Xunit;
using LsProto = global::Ts.LsProto;

namespace Ts.TestUtil.Fourslash;

public sealed partial class FourslashTest
{
    // ---------------------------------------------------------------------
    // fourslash.go:960-984 — VerifyCurrentFileContent / VerifyCurrentLineContent /
    // VerifyIndentation.
    // ---------------------------------------------------------------------

    public void VerifyCurrentFileContent(string expectedContent)
    {
        string actualContent = getScriptInfo(activeFilename)!.Content;
        Assert.Equal(expectedContent, actualContent);
    }

    public void VerifyCurrentLineContent(string expectedContent)
    {
        string actualContent = getScriptInfo(activeFilename)!
            .GetLineContent((int)currentCaretPosition.Line);
        Assert.True(
            string.Equals(actualContent, expectedContent, StringComparison.Ordinal),
            "\n  actual line: \"" + actualContent + "\"\nexpected line: \"" + expectedContent + "\"\n");
    }

    public void VerifyIndentation(int numSpaces)
    {
        // not implemented (fourslash.go:979-984 is a stub upstream too).
    }

    // ---------------------------------------------------------------------
    // fourslash.go:3977-4048 — the QuickInfo family.
    // ---------------------------------------------------------------------

    public void VerifyQuickInfoAt(string marker, string expectedText, string expectedDocumentation = "")
    {
        GoToMarker(marker);
        QuickInfo hover = getQuickInfoAtCurrentPosition();
        verifyHoverContent(hover, expectedText, expectedDocumentation, getCurrentPositionPrefix());
    }

    internal QuickInfo getQuickInfoAtCurrentPosition()
    {
        QuickInfo? result = LS().GetQuickInfoAtPosition(
            activeFilename,
            caretOffset(),
            cancellationToken);
        if (result == null)
        {
            throw new InvalidOperationException(
                "Expected hover result at marker '" + (lastKnownMarkerName ?? "<anonymous>") + "' but got nil");
        }
        return result;
    }

    // fourslash.go:3998-4022 — verifyHoverContent/verifyHoverMarkdown. SHAPE SEAM: the Go assert
    // pins the full hover markdown; the foundation surface is the QuickInfo record's fields.
    private void verifyHoverContent(QuickInfo hover, string expectedText, string expectedDocumentation, string prefix)
    {
        Assert.Equal(expectedText, hover.DisplayString);
        Assert.Equal(expectedDocumentation, hover.Documentation);
    }

    public void VerifyQuickInfoExists()
    {
        if (quickInfoIsEmpty().IsEmpty)
        {
            throw new InvalidOperationException(
                "Expected non-nil hover content at marker '" + (lastKnownMarkerName ?? "<anonymous>") + "'");
        }
    }

    public void VerifyNotQuickInfoExists()
    {
        (bool isEmpty, QuickInfo? hover) = quickInfoIsEmpty();
        if (!isEmpty)
        {
            throw new InvalidOperationException(
                "Expected empty hover content at marker '" + (lastKnownMarkerName ?? "<anonymous>") + "', got '"
                + hover!.DisplayString + "'");
        }
    }

    // fourslash.go:4036-4043 — quickInfoIsEmpty.
    private (bool IsEmpty, QuickInfo? Hover) quickInfoIsEmpty()
    {
        QuickInfo? hover = LS().GetQuickInfoAtPosition(activeFilename, caretOffset(), cancellationToken);
        if (hover == null || (hover.DisplayString.Length == 0 && hover.Documentation.Length == 0))
        {
            return (true, null);
        }
        return (false, hover);
    }

    public void VerifyQuickInfoIs(string expectedText, string expectedDocumentation = "")
    {
        QuickInfo hover = getQuickInfoAtCurrentPosition();
        verifyHoverContent(hover, expectedText, expectedDocumentation, getCurrentPositionPrefix());
    }

    // fourslash.go:4445-4450 — getCurrentPositionPrefix. (Go's exact prefix text is a test
    // failure message detail; this port keeps the marker/position information.)
    private string getCurrentPositionPrefix() =>
        "At marker '" + (lastKnownMarkerName ?? "<anonymous>") + "' (" + activeFilename + " "
        + currentCaretPosition.Line + ":" + currentCaretPosition.Character + "): ";

    // Raw UTF-16 offset of the caret in the active file (the C# LS position currency).
    private int caretOffset() =>
        converters.LineAndCharacterToPosition(getScriptInfo(activeFilename)!, currentCaretPosition);

    // ---------------------------------------------------------------------
    // fourslash.go:1011-1560 — completions. (MarkerInput = string | Marker | []string |
    // List<Marker> | nil, exactly Go's `string | *Marker | []string | []*Marker | nil`.)
    // ---------------------------------------------------------------------

    // fourslash.go:1011-1016 — CompletionsExpectedList. The option records are declared at
    // namespace level (Fourslash.Options.cs) so the emitted corpus can name them unqualified,
    // exactly as Go's corpus does.
    public CompletionList? VerifyCompletions(object? markerInput, CompletionsExpectedList? expected)
    {
        CompletionList? list = null;
        switch (markerInput)
        {
            case string markerName:
                GoToMarker(markerName);
                list = verifyCompletionsWorker(expected);
                break;
            case Marker marker:
                goToMarker(marker);
                list = verifyCompletionsWorker(expected);
                break;
            // fourslash.go:1070-1125 — the []string / []*Marker arms: every marker is visited and
            // verified, the last result being what Go returns to the caller.
            case string[] markerNames:
                foreach (string name in markerNames)
                {
                    GoToMarker(name);
                    list = verifyCompletionsWorker(expected);
                }
                break;
            case List<Marker> markersInput:
                foreach (Marker marker in markersInput)
                {
                    goToMarker(marker);
                    list = verifyCompletionsWorker(expected);
                }
                break;
            case null:
                list = verifyCompletionsWorker(expected);
                break;
            default:
                throw new InvalidOperationException(
                    "Invalid marker input type: " + markerInput.GetType().Name
                    + ". Expected string, Marker, or null.");
        }
        return list;
    }

    // fourslash.go:1126-1136 — verifyCompletionsWorker.
    private CompletionList? verifyCompletionsWorker(CompletionsExpectedList? expected)
    {
        string prefix = getCurrentPositionPrefix();
        CompletionList? list = getCompletions();
        verifyCompletionsResult(list, expected, prefix);
        return list;
    }

    // fourslash.go:1138-1141 + 1220-1241 — GetCompletions / getCompletions. The client-side
    // sort (server may return unsorted lists) rides a local copy of ls.CompareCompletionEntries
    // (internal in the C# port).
    public CompletionList? GetCompletions() => getCompletions();

    private CompletionList? getCompletions()
    {
        CompletionList? result = LS().GetCompletionsAtPosition(
            activeFilename,
            caretOffset(),
            triggerCharacter: null,
            includeSymbols: false,
            cancellationToken);
        // For performance, the server may return unsorted completion lists.
        // The client is expected to sort them by SortText and then by Label.
        // We are the client here.
        if (result?.Items is { } items)
        {
            // For performance, the server may return unsorted completion lists.
            // The client is expected to sort them by SortText and then by Label.
            // We are the client here. The raw API wraps each wire item with the
            // IncludeSymbols payload; includeSymbols=false keeps Item set.
            Array.Sort(items, static (a, b) => CompareCompletionEntries(a.Item, b.Item));
        }
        return result;
    }

    // completions.go:3252-3259 — CompareCompletionEntries (copy; the original is internal).
    private static int CompareCompletionEntries(LsProto.CompletionItem? a, LsProto.CompletionItem? b)
    {
        int result = global::Ts.StringUtil.StringUtil.CompareStringsCaseInsensitiveThenSensitive(a?.SortText ?? "", b?.SortText ?? "");
        if (result == global::Ts.StringUtil.StringUtil.ComparisonEqual)
        {
            result = global::Ts.StringUtil.StringUtil.CompareStringsCaseInsensitiveThenSensitive(a?.Label ?? "", b?.Label ?? "");
        }
        return result;
    }

    // fourslash.go:1243-1264 — verifyCompletionsResult.
    private void verifyCompletionsResult(CompletionList? actual, CompletionsExpectedList? expected, string prefix)
    {
        if (actual == null)
        {
            if (!isEmptyExpectedList(expected))
            {
                throw new InvalidOperationException(prefix + "Expected completion list but got nil.");
            }
            return;
        }
        if (expected == null)
        {
            if ((actual.Items?.Length ?? 0) == 0)
            {
                return;
            }
            throw new InvalidOperationException(prefix + "Expected nil completion list but got non-nil.");
        }
        Assert.Equal(expected.IsIncomplete, actual.IsIncomplete);
        verifyCompletionsItemDefaults(actual.ItemDefaults, expected.ItemDefaults, prefix + "ItemDefaults mismatch: ");
        // The raw API wraps wire items with the IncludeSymbols payload; unwrap to the lsproto
        // items the verifier compares (Go's list.Items are the wire items directly).
        var wireItems = new List<LsProto.CompletionItem>();
        if (actual.Items != null)
        {
            foreach (var wrapper in actual.Items)
            {
                if (wrapper.Item is { } item)
                {
                    wireItems.Add(item);
                }
            }
        }
        verifyCompletionsItems(prefix, [.. wireItems], expected.Items);
    }

    // fourslash.go:1266-1268 — isEmptyExpectedList.
    private static bool isEmptyExpectedList(CompletionsExpectedList? expected) =>
        expected == null ||
        (expected.Items == null ||
            ((expected.Items.Exact?.Count ?? 0) == 0 &&
             (expected.Items.Includes?.Count ?? 0) == 0 &&
             (expected.Items.Excludes?.Count ?? 0) == 0));

    // fourslash.go:1270-1307 — verifyCompletionsItemDefaults. EditRange is Ignored | *EditRange;
    // the *EditRange arm needs the range↔anonymous-marker link the baselines exercise and is a
    // recorded seam, so the corpus carries the Ignored sentinel (a no-op check).
    private static void verifyCompletionsItemDefaults(
        LsProto.CompletionItemDefaults? actual,
        CompletionsExpectedItemDefaults? expected,
        string prefix)
    {
        if (actual == null)
        {
            if (expected == null)
            {
                return;
            }
            throw new InvalidOperationException(prefix + "Expected non-nil completion item defaults but got nil");
        }
        if (expected == null)
        {
            throw new InvalidOperationException(prefix + "Expected nil completion item defaults but got non-nil");
        }
        Assert.True(
            actual.CommitCharacters == null && expected.CommitCharacters == null
            || actual.CommitCharacters != null && expected.CommitCharacters != null
                && actual.CommitCharacters.SequenceEqual(expected.CommitCharacters),
            prefix + "CommitCharacters mismatch");
        switch (expected.EditRange)
        {
            case null:
                if (actual.EditRange != null)
                {
                    throw new InvalidOperationException(prefix + "Expected nil EditRange but got non-nil");
                }
                break;
            case EditRange editRange:
                if (actual.EditRange == null)
                {
                    throw new InvalidOperationException(prefix + "Expected non-nil EditRange but got nil");
                }
                LsProto.Range expectedInsert = editRange.Insert.LSRange;
                LsProto.Range expectedReplace = editRange.Replace.LSRange;
                if (actual.EditRange.EditRangeWithInsertReplace is not { } actualEditRange ||
                    !RangesEqual(actualEditRange.Insert, expectedInsert) ||
                    !RangesEqual(actualEditRange.Replace, expectedReplace))
                {
                    throw new InvalidOperationException(prefix + "EditRange mismatch");
                }
                break;
            default:
                // Ignored (the corpus's sentinel: the anonymous-range linkage is not ported).
                break;
        }
    }

    private static bool RangesEqual(LsProto.Range a, LsProto.Range b) =>
        a.Start.Line == b.Start.Line && a.Start.Character == b.Start.Character &&
        a.End.Line == b.End.Line && a.End.Character == b.End.Character;

    // fourslash.go:1309-1460 — verifyCompletionsItems.
    private void verifyCompletionsItems(string prefix, LsProto.CompletionItem[] actual, CompletionsExpectedItems? expected)
    {
        if (expected == null)
        {
            return;
        }
        if (expected.Exact != null)
        {
            if (expected.Includes != null)
            {
                throw new InvalidOperationException(prefix + "Expected exact completion list but also specified 'includes'.");
            }
            if (expected.Excludes != null)
            {
                throw new InvalidOperationException(prefix + "Expected exact completion list but also specified 'excludes'.");
            }
            if (expected.Unsorted != null)
            {
                throw new InvalidOperationException(prefix + "Expected exact completion list but also specified 'unsorted'.");
            }
            if (actual.Length != expected.Exact.Count)
            {
                throw new InvalidOperationException(
                    prefix + "Expected " + expected.Exact.Count + " exact completion items but got " + actual.Length + ".");
            }
            if (actual.Length > 0)
            {
                verifyCompletionsAreExactly(prefix, actual, expected.Exact);
            }
            return;
        }

        var nameToActualItems = new Dictionary<string, List<LsProto.CompletionItem>>(StringComparer.Ordinal);
        foreach (LsProto.CompletionItem item in actual)
        {
            if (!nameToActualItems.TryGetValue(item.Label, out List<LsProto.CompletionItem>? bucket))
            {
                bucket = new List<LsProto.CompletionItem>();
                nameToActualItems[item.Label] = bucket;
            }
            bucket.Add(item);
        }

        if (expected.Unsorted != null)
        {
            if (expected.Includes != null)
            {
                throw new InvalidOperationException(prefix + "Expected unsorted completion list but also specified 'includes'.");
            }
            if (expected.Excludes != null)
            {
                throw new InvalidOperationException(prefix + "Expected unsorted completion list but also specified 'excludes'.");
            }
            foreach (object item in expected.Unsorted)
            {
                switch (item)
                {
                    case string label:
                        if (!nameToActualItems.Remove(label))
                        {
                            throw new InvalidOperationException(prefix + "Label '" + label + "' not found in actual items.");
                        }
                        break;
                    case LsProto.CompletionItem expectedItem:
                        verifyExpectedItemAgainstBuckets(prefix, nameToActualItems, expectedItem, removeOnSingle: true);
                        break;
                    default:
                        throw new InvalidOperationException(
                            prefix + "Expected completion item to be a string or CompletionItem, got " + item.GetType().Name);
                }
            }
            if (expected.Unsorted.Count != actual.Length)
            {
                string unmatched = string.Join("\n", nameToActualItems.Keys);
                throw new InvalidOperationException(
                    prefix + "Additional completions found but not included in 'unsorted': " + unmatched);
            }
            return;
        }

        if (expected.Includes != null)
        {
            foreach (object item in expected.Includes)
            {
                switch (item)
                {
                    case string label:
                        if (!nameToActualItems.ContainsKey(label))
                        {
                            throw new InvalidOperationException(prefix + "Label '" + label + "' not found in actual items.");
                        }
                        break;
                    case LsProto.CompletionItem expectedItem:
                        verifyExpectedItemAgainstBuckets(prefix, nameToActualItems, expectedItem, removeOnSingle: false);
                        break;
                    default:
                        throw new InvalidOperationException(
                            prefix + "Expected completion item to be a string or CompletionItem, got " + item.GetType().Name);
                }
            }
        }
        if (expected.Excludes != null)
        {
            foreach (string exclude in expected.Excludes)
            {
                if (nameToActualItems.ContainsKey(exclude))
                {
                    throw new InvalidOperationException(
                        prefix + "Label '" + exclude + "' should not be in actual items but was found.");
                }
            }
        }
    }

    // The Includes/Unsorted *lsproto.CompletionItem arm (fourslash.go:1347-1379 / :1399-1429):
    // find the first actual item with the same label that matches the expected fields.
    private static void verifyExpectedItemAgainstBuckets(
        string prefix,
        Dictionary<string, List<LsProto.CompletionItem>> nameToActualItems,
        LsProto.CompletionItem expectedItem,
        bool removeOnSingle)
    {
        if (!nameToActualItems.TryGetValue(expectedItem.Label, out List<LsProto.CompletionItem>? actualItems))
        {
            throw new InvalidOperationException(prefix + "Label '" + expectedItem.Label + "' not found in actual items.");
        }

        string mismatchPrefix = actualItems.Count > 1
            ? prefix + "No completion item match for label " + expectedItem.Label + " (multiple candidates found): "
            : prefix + "Includes completion item mismatch for label " + expectedItem.Label + ": ";

        int itemIndex = actualItems.FindIndex(actualItem => verifyCompletionItem(actualItem, expectedItem) == "");
        // fail test if no match found
        if (itemIndex == -1)
        {
            throw new InvalidOperationException(mismatchPrefix);
        }

        // delete previous entries since we verify entries in order
        if (removeOnSingle)
        {
            if (actualItems.Count == 1)
            {
                nameToActualItems.Remove(expectedItem.Label);
            }
            else if (itemIndex == 0)
            {
                actualItems.RemoveAt(0);
            }
            else if (itemIndex == actualItems.Count - 1)
            {
                actualItems.RemoveAt(itemIndex);
            }
            else
            {
                actualItems.RemoveAt(itemIndex);
            }
        }
        else
        {
            if (actualItems.Count == 1 || itemIndex == actualItems.Count - 1)
            {
                nameToActualItems.Remove(expectedItem.Label);
            }
            else
            {
                actualItems.RemoveAt(0);
            }
        }
    }

    // fourslash.go:1442-1460 — verifyCompletionsAreExactly.
    private static void verifyCompletionsAreExactly(
        string prefix,
        LsProto.CompletionItem[] actual,
        List<object> expected)
    {
        for (int i = 0; i < expected.Count; i++)
        {
            LsProto.CompletionItem expectedItem = getExpectedLabel(expected[i]);
            string err = verifyCompletionItem(actual[i], expectedItem);
            if (err != "")
            {
                throw new InvalidOperationException(prefix + "Completion item " + i + " mismatch: " + err);
            }
        }
    }

    // fourslash.go:1476-1538 — verifyCompletionItem. Go's cmp walks every set field of the
    // expected item; this port covers the fields the ported corpus sets (label always, plus
    // kind/sort/filter/detail/insertText and the auto-import data when present).
    private static string verifyCompletionItem(LsProto.CompletionItem actual, LsProto.CompletionItem expected)
    {
        var mismatches = new List<string>();
        if (!string.Equals(actual.Label, expected.Label, StringComparison.Ordinal))
        {
            mismatches.Add("label: expected \"" + expected.Label + "\", got \"" + actual.Label + "\"");
        }
        bool actualAutoImportFix = actual.Data?.AutoImport != null;
        bool expectedAutoImportFix = expected.Data?.AutoImport != null;
        if (actualAutoImportFix != expectedAutoImportFix)
        {
            return "Mismatch in auto-import data presence";
        }
        if (actualAutoImportFix)
        {
            // The auto-import arm ignores the resolved-detail surface (fourslash.go:1486-1516).
            return string.Join("; ", mismatches);
        }
        if (expected.Kind.HasValue && actual.Kind != expected.Kind)
        {
            mismatches.Add("kind: expected " + expected.Kind.Value + ", got " + (actual.Kind?.ToString() ?? "null"));
        }
        if (expected.SortText != null && !string.Equals(actual.SortText, expected.SortText, StringComparison.Ordinal))
        {
            mismatches.Add("sortText: expected \"" + expected.SortText + "\", got \"" + actual.SortText + "\"");
        }
        if (expected.FilterText != null && !string.Equals(actual.FilterText, expected.FilterText, StringComparison.Ordinal))
        {
            mismatches.Add("filterText: expected \"" + expected.FilterText + "\", got \"" + actual.FilterText + "\"");
        }
        if (expected.Detail != null && !string.Equals(actual.Detail, expected.Detail, StringComparison.Ordinal))
        {
            mismatches.Add("detail: expected \"" + expected.Detail + "\", got \"" + actual.Detail + "\"");
        }
        if (expected.InsertText != null && !string.Equals(actual.InsertText, expected.InsertText, StringComparison.Ordinal))
        {
            mismatches.Add("insertText: expected \"" + expected.InsertText + "\", got \"" + actual.InsertText + "\"");
        }
        if (expected.AdditionalTextEdits != null &&
            !ReferenceEquals(expected.AdditionalTextEdits, AnyTextEdits))
        {
            mismatches.Add("additionalTextEdits: expected unset, got "
                + (actual.AdditionalTextEdits == null ? "null" : actual.AdditionalTextEdits.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return string.Join("; ", mismatches);
    }

    // fourslash.go:1549-1560 — getExpectedLabel.
    private static LsProto.CompletionItem getExpectedLabel(object item)
    {
        switch (item)
        {
            case string label:
                return new LsProto.CompletionItem { Label = label };
            case LsProto.CompletionItem completionItem:
                return completionItem;
            default:
                throw new InvalidOperationException(
                    "Expected completion item to be a string or CompletionItem, got " + item.GetType().Name);
        }
    }

    // fourslash.go:1539-1548 — ResolveCompletionItem. (Data rides the item; the C# LS entry
    // takes it explicitly.)
    public LsProto.CompletionItem ResolveCompletionItem(LsProto.CompletionItem item)
    {
        return LS().ResolveCompletionItem(item, item.Data, cancellationToken);
    }

    // ---------------------------------------------------------------------
    // fourslash.go:4575-4841 — rename.
    // ---------------------------------------------------------------------

    // fourslash.go:4657-4685 — VerifyRenameSucceeded (rename.go:47/:33 currency: DocumentUri +
    // lsproto.Position through l.converters). Go's `defer f.ConfigureWithReset(t, *preferences)()`
    // maps to ConfigureWithPreferences/RestorePreferences in try/finally.
    public void VerifyRenameSucceeded(UserPreferences? preferences = null)
    {
        UserPreferences? saved = ConfigureWithPreferences(preferences);
        try
        {
            string prefix = getCurrentPositionPrefix();
            global::Ts.Core.RequestContext ctx = RenameContext();
            RenameInfo info = LS().GetRenameInfo(
                ctx,
                "" /*newName*/,
                DocumentUriFor(activeFilename),
                currentCaretPosition);
            if (!info.CanRename)
            {
                throw new InvalidOperationException(prefix + "Expected rename to succeed, but prepareRename returned null");
            }

            // Also verify that textDocument/rename produces edits, since prepareRename is optional.
            LsProto.WorkspaceEditOrNull renameResult = LS().ProvideRename(
                ctx,
                new LsProto.RenameParams
                {
                    TextDocument = new LsProto.TextDocumentIdentifier { Uri = DocumentUriFor(activeFilename) },
                    Position = currentCaretPosition,
                    NewName = "RENAME_SUCCEEDED_TEST",
                },
                orchestrator: null);
            LsProto.WorkspaceEdit? edit = renameResult.WorkspaceEdit;
            if (edit == null || edit.Changes == null || edit.Changes.Count == 0)
            {
                throw new InvalidOperationException(prefix + "prepareRename succeeded but textDocument/rename returned no changes");
            }
        }
        finally
        {
            RestorePreferences(saved);
        }
    }

    // fourslash.go:4970-5008 — VerifyRenameFailed. (prepareRename's error response / null result
    // is RenameInfo.CanRename == false on the in-process transport, D-012.)
    public void VerifyRenameFailed(UserPreferences? preferences = null)
    {
        UserPreferences? saved = ConfigureWithPreferences(preferences);
        try
        {
            string prefix = getCurrentPositionPrefix();
            global::Ts.Core.RequestContext ctx = RenameContext();
            RenameInfo info = LS().GetRenameInfo(
                ctx,
                "" /*newName*/,
                DocumentUriFor(activeFilename),
                currentCaretPosition);
            if (info.CanRename)
            {
                throw new InvalidOperationException(prefix + "Expected rename to fail, but prepareRename returned a result");
            }

            // Also verify that textDocument/rename does not produce usable edits, since prepareRename is optional.
            LsProto.WorkspaceEditOrNull renameResult = LS().ProvideRename(
                ctx,
                new LsProto.RenameParams
                {
                    TextDocument = new LsProto.TextDocumentIdentifier { Uri = DocumentUriFor(activeFilename) },
                    Position = currentCaretPosition,
                    NewName = "RENAME_FAILED_TEST",
                },
                orchestrator: null);
            LsProto.WorkspaceEdit? edit = renameResult.WorkspaceEdit;
            if (edit != null && edit.Changes != null && edit.Changes.Count > 0)
            {
                throw new InvalidOperationException(prefix + "prepareRename returned null but textDocument/rename returned changes");
            }
        }
        finally
        {
            RestorePreferences(saved);
        }
    }

    // fourslash.go:4687-4757 — RenameAtCaret. (The DocumentChanges/RenameFile arms of the
    // workspace edit are omitted — the C# ProvideRename produces the Changes arm; file renames
    // ride FileRename/willRenameFiles which is not ported.)
    public LsProto.WorkspaceEdit? RenameAtCaret(string newName)
    {
        LsProto.WorkspaceEditOrNull renameResult = LS().ProvideRename(
            RenameContext(),
            new LsProto.RenameParams
            {
                TextDocument = new LsProto.TextDocumentIdentifier { Uri = DocumentUriFor(activeFilename) },
                Position = currentCaretPosition,
                NewName = newName,
            },
            orchestrator: null);
        LsProto.WorkspaceEdit? result = renameResult.WorkspaceEdit;

        if (result == null)
        {
            return result;
        }

        if (result.Changes != null)
        {
            // map-iter: 序无关 — each file's edits are independent; per-file application order
            // cannot affect other files.
            foreach (KeyValuePair<LsProto.DocumentUri, LsProto.TextEdit[]> entry in result.Changes)
            {
                string fileName = DocumentUriToFileName(entry.Key);
                FourslashScriptInfo script = getOrLoadScriptInfo(fileName)
                    ?? throw new InvalidOperationException("Script info for file " + fileName + " not found");
                var changes = new List<TextChange>(entry.Value.Length);
                foreach (LsProto.TextEdit edit in entry.Value)
                {
                    changes.Add(new TextChange(converters.FromLSPRange(script, edit.Range), edit.NewText));
                }
                editScriptAndUpdateMarkersWorker(fileName, changes);
            }
        }

        return result;
    }

    // The rename entries take an LSP request context; the harness carries the cancellation
    // token separately.
    private global::Ts.Core.RequestContext RenameContext() =>
        global::Ts.Core.RequestContext.Background with { Cancellation = cancellationToken };

    // fourslash.go:4830-4841 — VerifyRename.
    public void VerifyRename(string markerName, string newName, IReadOnlyDictionary<string, string> expectedFileContents)
    {
        GoToMarker(markerName);
        RenameAtCaret(newName);
        // map-iter: 序无关 — independent file assertions.
        foreach (KeyValuePair<string, string> entry in expectedFileContents)
        {
            FourslashScriptInfo? script = getScriptInfo(entry.Key);
            if (script == null)
            {
                throw new InvalidOperationException("Expected script info for " + entry.Key + ", but got nil");
            }
            Assert.True(
                string.Equals(script.Content, entry.Value, StringComparison.Ordinal),
                "File content after rename did not match expected content for " + entry.Key + ".");
        }
    }

    // lsconv.FileNameToDocumentURI — the DocumentUri the rename entries take (Go:
    // lsconv.FileNameToDocumentURI(f.activeFilename)).
    private static LsProto.DocumentUri DocumentUriFor(string fileName) =>
        global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(fileName);

    // lsconv.DocumentUri.FileName() equivalent for the simple file:// URIs the harness sees.
    private static string DocumentUriToFileName(LsProto.DocumentUri uri)
    {
        string value = uri.Value;
        const string prefix = "file://";
        if (value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Uri.UnescapeDataString(value[prefix.Length..]);
        }
        return Uri.UnescapeDataString(value);
    }

    // ---------------------------------------------------------------------
    // fourslash.go:5218-5272, :5632-5777 — diagnostics.
    // ---------------------------------------------------------------------

    public void VerifyDiagnostics(LsProto.Diagnostic[] expected)
    {
        verifyDiagnostics(expected, static _ => true);
    }

    // Similar to `VerifyDiagnostics`, but excludes suggestion diagnostics returned from server.
    public void VerifyNonSuggestionDiagnostics(LsProto.Diagnostic[] expected)
    {
        verifyDiagnostics(expected, static diag => !isSuggestionDiagnostic(diag));
    }

    // Similar to `VerifyDiagnostics`, but includes only suggestion diagnostics returned from server.
    public void VerifySuggestionDiagnostics(LsProto.Diagnostic[] expected)
    {
        verifyDiagnostics(expected, isSuggestionDiagnostic);
    }

    private void verifyDiagnostics(LsProto.Diagnostic[] expected, Func<LsProto.Diagnostic, bool> filterDiagnostics)
    {
        List<LsProto.Diagnostic> actualDiagnostics = getDiagnostics(activeFilename).Where(filterDiagnostics).ToList();
        // Go compares the raw lsproto.Range struct; Position/Range are classes here, so the
        // "empty range" sentinel is compared field-wise.
        static bool isEmptyRange(LsProto.Range range) =>
            range.Start.Line == 0 && range.Start.Character == 0 &&
            range.End.Line == 0 && range.End.Character == 0;
        var expectedWithRanges = new List<LsProto.Diagnostic>(expected.Length);
        foreach (LsProto.Diagnostic diag in expected)
        {
            if (isEmptyRange(diag.Range))
            {
                List<RangeMarker> rangesInFile = getRangesInFile(activeFilename);
                if (rangesInFile.Count == 0)
                {
                    throw new InvalidOperationException(
                        "No ranges found in file " + activeFilename + " to assign to diagnostic with empty range");
                }
                var diagWithRange = new LsProto.Diagnostic
                {
                    Range = rangesInFile[0].LSRange,
                    Severity = diag.Severity,
                    Code = diag.Code,
                    Source = diag.Source,
                    Message = diag.Message,
                    Tags = diag.Tags,
                };
                expectedWithRanges.Add(diagWithRange);
            }
            else
            {
                expectedWithRanges.Add(diag);
            }
        }
        if (actualDiagnostics.Count == 0 && expectedWithRanges.Count == 0)
        {
            return;
        }
        Assert.True(
            actualDiagnostics.Count == expectedWithRanges.Count
            && actualDiagnostics.Zip(expectedWithRanges, (a, e) =>
                a.Range.Start.Line == e.Range.Start.Line &&
                a.Range.Start.Character == e.Range.Start.Character &&
                a.Range.End.Line == e.Range.End.Line &&
                a.Range.End.Character == e.Range.End.Character &&
                DiagnosticMessage(a) == DiagnosticMessage(e)).All(static same => same),
            "Diagnostics do not match expected:\n  actual:   " + FormatDiagnostics(actualDiagnostics)
            + "\n  expected: " + FormatDiagnostics(expectedWithRanges));
    }

    private static string DiagnosticMessage(LsProto.Diagnostic diag) =>
        diag.Message.String ?? diag.Message.MarkupContent?.Value ?? "";

    private static string FormatDiagnostics(IReadOnlyList<LsProto.Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(static diag =>
            "  [" + diag.Range.Start.Line + ":" + diag.Range.Start.Character + "-"
            + diag.Range.End.Line + ":" + diag.Range.End.Character + "] "
            + diag.Severity + " " + (diag.Code?.Integer?.ToString() ?? diag.Code?.String ?? "") + " "
            + DiagnosticMessage(diag)));

    // fourslash.go:5256-5267 — getDiagnostics.
    public List<LsProto.Diagnostic> getDiagnostics(string fileName) =>
        [.. LS().ProvideDiagnostics(fileName, reportStyleChecksAsWarnings: false, cancellationToken)];

    // fourslash.go:5269-5271 — isSuggestionDiagnostic.
    private static bool isSuggestionDiagnostic(LsProto.Diagnostic diag) =>
        diag.Severity != null && diag.Severity == LsProto.DiagnosticSeverity.Hint;

    // VerifyNumberOfErrorsInCurrentFile verifies that the current file has the expected number of errors.
    public void VerifyNumberOfErrorsInCurrentFile(int expectedCount)
    {
        List<LsProto.Diagnostic> diagnostics = getDiagnostics(activeFilename);
        // Filter to only include errors (not suggestions/hints)
        int errors = diagnostics.Count(static diag => !isSuggestionDiagnostic(diag));
        if (errors != expectedCount)
        {
            throw new InvalidOperationException(
                "Expected " + expectedCount + " errors in current file, but got " + errors);
        }
    }

    // VerifyNoErrors verifies that no errors exist in any open files.
    public void VerifyNoErrors()
    {
        // map-iter: 序无关 — every open file must be clean; iteration order only affects which
        // failure message appears first.
        foreach (string fileName in openFiles)
        {
            List<LsProto.Diagnostic> diagnostics = getDiagnostics(fileName);
            // Filter to only include errors (not suggestions/hints)
            List<LsProto.Diagnostic> errors = diagnostics.Where(static diag => !isSuggestionDiagnostic(diag)).ToList();
            if (errors.Count > 0)
            {
                string messages = string.Join(", ", errors.Select(static err => DiagnosticMessage(err)));
                throw new InvalidOperationException(
                    "Expected no errors but found " + errors.Count + " in " + fileName + ": " + messages);
            }
        }
    }

    // VerifyErrorExistsAtRange verifies that an error with the given code exists at the given range.
    public void VerifyErrorExistsAtRange(RangeMarker rangeMarker, int code, string message)
    {
        List<LsProto.Diagnostic> diagnostics = getDiagnostics(rangeMarker.FileName);
        foreach (LsProto.Diagnostic diag in diagnostics)
        {
            if (diag.Code != null && diag.Code.Integer != null && diag.Code.Integer.Value == code)
            {
                // Check if the range matches
                if (diag.Range.Start.Line == rangeMarker.LSRange.Start.Line &&
                    diag.Range.Start.Character == rangeMarker.LSRange.Start.Character &&
                    diag.Range.End.Line == rangeMarker.LSRange.End.Line &&
                    diag.Range.End.Character == rangeMarker.LSRange.End.Character)
                {
                    // If message is provided, verify it matches
                    if (message.Length != 0 && DiagnosticMessage(diag) != message)
                    {
                        throw new InvalidOperationException(
                            "Error at range has code " + code + " but message mismatch. Expected: \""
                            + message + "\", Got: \"" + DiagnosticMessage(diag) + "\"");
                    }
                    return;
                }
            }
        }
        throw new InvalidOperationException(
            "Expected error with code " + code + " at range " + FormatLspRange(rangeMarker.LSRange)
            + " but it was not found");
    }

    // VerifyErrorExistsBetweenMarkers verifies that an error exists between the two markers.
    public void VerifyErrorExistsBetweenMarkers(string startMarkerName, string endMarkerName)
    {
        Marker startMarker = MarkerByName(startMarkerName);
        Marker endMarker = MarkerByName(endMarkerName);
        if (startMarker.FileName != endMarker.FileName)
        {
            throw new InvalidOperationException("Markers '" + startMarkerName + "' and '" + endMarkerName + "' are in different files");
        }

        List<LsProto.Diagnostic> diagnostics = getDiagnostics(startMarker.FileName);
        int startPos = startMarker.Position;
        int endPos = endMarker.Position;

        foreach (LsProto.Diagnostic diag in diagnostics)
        {
            if (!isSuggestionDiagnostic(diag))
            {
                FourslashScriptInfo script = getScriptInfo(startMarker.FileName)!;
                int diagStart = converters.LineAndCharacterToPosition(script, diag.Range.Start);
                int diagEnd = converters.LineAndCharacterToPosition(script, diag.Range.End);
                if (diagStart >= startPos && diagEnd <= endPos)
                {
                    return; // Found an error in the range
                }
            }
        }
        throw new InvalidOperationException(
            "Expected error between markers '" + startMarkerName + "' and '" + endMarkerName + "' but none was found");
    }

    // VerifyErrorExistsAfterMarker verifies that an error exists after the given marker.
    public void VerifyErrorExistsAfterMarker(string markerName)
    {
        string fileName;
        int markerPos;

        if (markerName.Length == 0)
        {
            // Use current position
            fileName = activeFilename;
            markerPos = caretOffset();
        }
        else
        {
            Marker marker = MarkerByName(markerName);
            fileName = marker.FileName;
            markerPos = marker.Position;
        }

        List<LsProto.Diagnostic> diagnostics = getDiagnostics(fileName);

        foreach (LsProto.Diagnostic diag in diagnostics)
        {
            if (!isSuggestionDiagnostic(diag))
            {
                int diagStart = converters.LineAndCharacterToPosition(getScriptInfo(fileName)!, diag.Range.Start);
                if (diagStart >= markerPos)
                {
                    return; // Found an error after the marker
                }
            }
        }
        throw new InvalidOperationException("Expected error after marker '" + markerName + "' but none was found");
    }

    // VerifyErrorExistsBeforeMarker verifies that an error exists before the given marker.
    public void VerifyErrorExistsBeforeMarker(string markerName)
    {
        string fileName;
        int markerPos;

        if (markerName.Length == 0)
        {
            // Use current position
            fileName = activeFilename;
            markerPos = caretOffset();
        }
        else
        {
            Marker marker = MarkerByName(markerName);
            fileName = marker.FileName;
            markerPos = marker.Position;
        }

        List<LsProto.Diagnostic> diagnostics = getDiagnostics(fileName);

        foreach (LsProto.Diagnostic diag in diagnostics)
        {
            if (!isSuggestionDiagnostic(diag))
            {
                int diagEnd = converters.LineAndCharacterToPosition(getScriptInfo(fileName)!, diag.Range.End);
                if (diagEnd <= markerPos)
                {
                    return; // Found an error before the marker
                }
            }
        }
        throw new InvalidOperationException("Expected error before marker '" + markerName + "' but none was found");
    }

    private static string FormatLspRange(LsProto.Range range) =>
        range.Start.Line + ":" + range.Start.Character + "-" + range.End.Line + ":" + range.End.Character;

    // ---------------------------------------------------------------------
    // fourslash.go:4122-4450 — signature help. The expected-options record lives at namespace
    // level (Fourslash.Options.cs) so the corpus can name it unqualified, matching Go.
    // ---------------------------------------------------------------------

    public void VerifySignatureHelp(VerifySignatureHelpOptions expected)
    {
        SignatureHelp? actual = LS().GetSignatureHelpItems(activeFilename, caretOffset(), cancellationToken);
        verifySignatureHelpResult(actual, expected, getCurrentPositionPrefix());
    }

    public void VerifyNoSignatureHelp() =>
        VerifyNoSignatureHelpWithContext(context: null);

    // fourslash.go:4308-4340 — VerifyNoSignatureHelp / VerifyNoSignatureHelpWithContext.
    public void VerifyNoSignatureHelpWithContext(LsProto.SignatureHelpContext? context)
    {
        _ = context; // The direct-program host carries no request context (LS takes no context arg).
        VerifyNoSignatureHelpWorker();
    }

    // fourslash.go:4341-4349 — VerifyNoSignatureHelpForMarkersWithContext.
    public void VerifyNoSignatureHelpForMarkersWithContext(LsProto.SignatureHelpContext? context, params string[] markers)
    {
        _ = context;
        VerifyNoSignatureHelpForMarkers(markers);
    }

    // fourslash.go:4376-4391 — VerifyNoSignatureHelpForMarkers.
    public void VerifyNoSignatureHelpForMarkers(params string[] markers)
    {
        if (markers.Length == 0)
        {
            VerifyNoSignatureHelpWorker();
            return;
        }
        foreach (string marker in markers)
        {
            GoToMarker(marker);
            VerifyNoSignatureHelpWorker();
        }
    }

    private void VerifyNoSignatureHelpWorker()
    {
        SignatureHelp? actual = LS().GetSignatureHelpItems(activeFilename, caretOffset(), cancellationToken);
        if (actual != null && actual.Signatures.Length != 0)
        {
            throw new InvalidOperationException(
                getCurrentPositionPrefix() + "Expected no signature help, got " + actual.Signatures.Length + " signatures");
        }
    }

    // fourslash.go:4350-4375 — VerifySignatureHelpPresent / VerifySignatureHelpPresentForMarkers.
    public void VerifySignatureHelpPresent(LsProto.SignatureHelpContext? context = null)
    {
        _ = context;
        VerifySignatureHelpPresentWorker();
    }

    public void VerifySignatureHelpPresentForMarkers(LsProto.SignatureHelpContext? context, params string[] markers)
    {
        _ = context;
        foreach (string marker in markers)
        {
            GoToMarker(marker);
            VerifySignatureHelpPresentWorker();
        }
    }

    private void VerifySignatureHelpPresentWorker()
    {
        SignatureHelp? actual = LS().GetSignatureHelpItems(activeFilename, caretOffset(), cancellationToken);
        if (actual == null || actual.Signatures.Length == 0)
        {
            throw new InvalidOperationException(
                getCurrentPositionPrefix() + "Expected signature help to be present, got none");
        }
    }

    // fourslash.go:4163-4307 — verifySignatureHelpResult. The C# foundation record carries
    // Label strings rather than label-offset spans, so ParameterName/ParameterSpan compare the
    // parameter label (the "x: string" -> "x" extraction Go performs on the label is kept).
    private void verifySignatureHelpResult(SignatureHelp? help, VerifySignatureHelpOptions expected, string prefix)
    {
        if (help == null)
        {
            throw new InvalidOperationException(prefix + "Could not get signature help");
        }

        // Determine which signature to check.
        int selectedIndex = 0;
        if (expected.OverrideSelectedItemIndex > 0)
        {
            selectedIndex = expected.OverrideSelectedItemIndex;
        }
        else if (help.ActiveSignature >= 0)
        {
            selectedIndex = help.ActiveSignature;
        }

        if (selectedIndex >= help.Signatures.Length)
        {
            throw new InvalidOperationException(
                prefix + "Selected signature index " + selectedIndex + " out of range (have " + help.Signatures.Length + " signatures)");
        }

        SignatureInformation selectedSig = help.Signatures[selectedIndex];
        var mismatches = new List<string>();

        if (expected.OverloadsCount > 0 && help.Signatures.Length != expected.OverloadsCount)
        {
            mismatches.Add($"Expected {expected.OverloadsCount} overloads, got {help.Signatures.Length}");
        }
        if (expected.Text.Length != 0 && !string.Equals(selectedSig.Label, expected.Text, StringComparison.Ordinal))
        {
            mismatches.Add($"Expected signature text \"{expected.Text}\", got \"{selectedSig.Label}\"");
        }
        // DocComment: the foundation SignatureInformation record carries no documentation field
        // (Recorded seam — the assertion is skipped when the corpus expects documentation).
        if (expected.ParameterCount > 0 && selectedSig.Parameters.Length != expected.ParameterCount)
        {
            mismatches.Add($"Expected {expected.ParameterCount} parameters, got {selectedSig.Parameters.Length}");
        }

        int activeParamIndex = help.ActiveParameter ?? -1;
        ParameterInformation? activeParam =
            activeParamIndex >= 0 && activeParamIndex < selectedSig.Parameters.Length
                ? selectedSig.Parameters[activeParamIndex]
                : null;

        if (expected.ParameterName.Length != 0)
        {
            if (activeParam == null)
            {
                mismatches.Add($"Expected parameter name \"{expected.ParameterName}\", but no active parameter");
            }
            else
            {
                string label = activeParam.Label;
                if (label.StartsWith("...", StringComparison.Ordinal))
                {
                    label = label[3..];
                }
                string actualName;
                int colon = label.IndexOf(':', StringComparison.Ordinal);
                int extends = label.IndexOf(" extends ", StringComparison.Ordinal);
                if (colon >= 0)
                {
                    actualName = label[..colon].Trim();
                }
                else if (extends >= 0)
                {
                    actualName = label[..extends].Trim();
                }
                else
                {
                    actualName = label;
                }
                if (!string.Equals(actualName, expected.ParameterName, StringComparison.Ordinal))
                {
                    mismatches.Add($"Expected parameter name \"{expected.ParameterName}\", got \"{actualName}\"");
                }
            }
        }
        if (expected.ParameterSpan.Length != 0)
        {
            if (activeParam == null)
            {
                mismatches.Add($"Expected parameter span \"{expected.ParameterSpan}\", but no active parameter");
            }
            else if (!string.Equals(activeParam.Label, expected.ParameterSpan, StringComparison.Ordinal))
            {
                mismatches.Add($"Expected parameter span \"{expected.ParameterSpan}\", got \"{activeParam.Label}\"");
            }
        }
        // ParameterDocComment: no documentation field on the foundation ParameterInformation
        // record (recorded seam; same shape as DocComment above).
        if (expected.IsVariadicSet)
        {
            bool actualIsVariadic = false;
            foreach (ParameterInformation param in selectedSig.Parameters)
            {
                if (param.Label.StartsWith("...", StringComparison.Ordinal))
                {
                    actualIsVariadic = true;
                    break;
                }
            }
            if (actualIsVariadic != expected.IsVariadic)
            {
                mismatches.Add($"Expected isVariadic={expected.IsVariadic}, got {actualIsVariadic}");
            }
        }

        if (mismatches.Count != 0)
        {
            Assert.Fail(prefix + string.Join("\n", mismatches));
        }
    }

    // ---------------------------------------------------------------------
    // internal/fourslash/semantictokens.go — VerifySemanticTokens (whole file). The SemanticToken
    // record is declared at namespace level (Fourslash.Options.cs) so the corpus can name it
    // unqualified, matching Go.
    // ---------------------------------------------------------------------

    public void VerifySemanticTokens(IReadOnlyList<SemanticToken> expected)
    {
        LsProto.SemanticTokens? result = LS().ProvideSemanticTokens(
            activeFilename,
            new LsProto.ResolvedSemanticTokensClientCapabilities
            {
                TokenTypes = semanticTokenTypes,
                TokenModifiers = semanticTokenModifiers,
            },
            cancellationToken);

        if (result == null)
        {
            if (expected.Count == 0)
            {
                return;
            }
            throw new InvalidOperationException("Expected semantic tokens but got nil");
        }

        // Decode the semantic tokens using token types/modifiers from the test configuration
        List<SemanticToken> actual = DecodeSemanticTokens(result.Data, semanticTokenTypes, semanticTokenModifiers);

        // Compare with expected
        if (actual.Count != expected.Count)
        {
            throw new InvalidOperationException(
                "Expected " + expected.Count + " semantic tokens, got " + actual.Count
                + "\n\nExpected:\n" + FormatSemanticTokens(expected) + "\n\nActual:\n" + FormatSemanticTokens(actual));
        }

        for (int i = 0; i < expected.Count; i++)
        {
            SemanticToken exp = expected[i];
            SemanticToken act = actual[i];
            if (exp.Type != act.Type || exp.Text != act.Text)
            {
                throw new InvalidOperationException(
                    "Token " + i + " mismatch:\n  Expected: {Type: \"" + exp.Type + "\", Text: \"" + exp.Text + "\"}"
                    + "\n  Actual:   {Type: \"" + act.Type + "\", Text: \"" + act.Text + "\"}");
            }
        }
    }

    // semantictokens.go:55-122 — decodeSemanticTokens.
    private List<SemanticToken> DecodeSemanticTokens(uint[] data, string[] tokenTypes, string[] tokenModifiers)
    {
        if (data.Length % 5 != 0)
        {
            throw new InvalidOperationException("Invalid semantic tokens data length: " + data.Length);
        }

        FourslashScriptInfo scriptInfo = scriptInfos[activeFilename];
        var tokens = new List<SemanticToken>();
        uint prevLine = 0;
        uint prevChar = 0;

        for (int i = 0; i < data.Length; i += 5)
        {
            uint deltaLine = data[i];
            uint deltaChar = data[i + 1];
            uint length = data[i + 2];
            uint tokenTypeIdx = data[i + 3];
            uint tokenModifierMask = data[i + 4];

            // Calculate absolute position
            uint line = prevLine + deltaLine;
            uint charPos = deltaLine == 0 ? prevChar + deltaChar : deltaChar;

            // Get token type
            if (tokenTypeIdx >= (uint)tokenTypes.Length)
            {
                throw new InvalidOperationException("Token type index out of range: " + tokenTypeIdx);
            }
            string tokenType = tokenTypes[tokenTypeIdx];

            // Get modifiers
            var modifiers = new List<string>();
            for (int m = 0; m < tokenModifiers.Length; m++)
            {
                if ((tokenModifierMask & (1u << m)) != 0)
                {
                    modifiers.Add(tokenModifiers[m]);
                }
            }

            // Build full type string (type.modifier1.modifier2)
            string typeStr = tokenType;
            if (modifiers.Count > 0)
            {
                typeStr = typeStr + "." + string.Join(".", modifiers);
            }

            // Get the text
            var startPos = new LsProto.Position { Line = line, Character = charPos };
            var endPos = new LsProto.Position { Line = line, Character = charPos + length };
            int startOffset = converters.LineAndCharacterToPosition(scriptInfo, startPos);
            int endOffset = converters.LineAndCharacterToPosition(scriptInfo, endPos);
            string text = scriptInfo.Content[startOffset..endOffset];

            tokens.Add(new SemanticToken(typeStr, text));

            prevLine = line;
            prevChar = charPos;
        }

        return tokens;
    }

    // semantictokens.go:124-130 — formatSemanticTokens.
    private static string FormatSemanticTokens(IReadOnlyList<SemanticToken> tokens) =>
        string.Join("\n", tokens.Select(static (tok, i) =>
            "  [" + i + "] {Type: \"" + tok.Type + "\", Text: \"" + tok.Text + "\"}"));

    // ---------------------------------------------------------------------
    // fourslash.go:5191-5216 — VerifyLinkedEditing / VerifyLinkedEditingHasNone.
    // ---------------------------------------------------------------------

    // fourslash.go:5191-5216 — VerifyLinkedEditing. A `null` value means the response must have
    // no entry for that marker, which is a different assertion from an empty range list and is
    // checked by VerifyLinkedEditingHasNone below.
    public void VerifyLinkedEditing(IReadOnlyDictionary<string, LsProto.Range[]?> markerNamesToExpected)
    {
        // map-iter: 序无关 — each marker is verified independently.
        foreach (KeyValuePair<string, LsProto.Range[]?> entry in markerNamesToExpected)
        {
            GoToMarker(entry.Key);
            LsProto.LinkedEditingRanges? actualRanges = LS().ProvideLinkedEditingRange(
                activeFilename,
                caretOffset());
            if (entry.Value == null)
            {
                throw new InvalidOperationException(
                    "Marker '" + entry.Key + "' expects no linked editing entry; call VerifyLinkedEditingHasNone.");
            }
            if (actualRanges == null)
            {
                throw new InvalidOperationException(
                    "Expected linked editing ranges for marker '" + entry.Key + "' but got nil");
            }
            LsProto.Range[] expectedRanges = entry.Value;
            Assert.True(
                actualRanges.Ranges.Length == expectedRanges.Length,
                "Linked editing range count for marker '" + entry.Key + "' does not match expected: got "
                + actualRanges.Ranges.Length + ", expected " + expectedRanges.Length);
            for (int i = 0; i < expectedRanges.Length && i < actualRanges.Ranges.Length; i++)
            {
                Assert.True(
                    actualRanges.Ranges[i].Start.Line == expectedRanges[i].Start.Line &&
                    actualRanges.Ranges[i].Start.Character == expectedRanges[i].Start.Character &&
                    actualRanges.Ranges[i].End.Line == expectedRanges[i].End.Line &&
                    actualRanges.Ranges[i].End.Character == expectedRanges[i].End.Character,
                    "Linked editing range " + i + " for marker '" + entry.Key + "' does not match expected");
            }
        }
    }

    // The `= null` entries of the corpus's marker→range map: the response must have no entry.
    public void VerifyLinkedEditingHasNone(IReadOnlyDictionary<string, LsProto.Range[]?> markerNamesToExpected)
    {
        // map-iter: 序无关 — each marker is verified independently.
        foreach (KeyValuePair<string, LsProto.Range[]?> entry in markerNamesToExpected)
        {
            if (entry.Value != null)
            {
                continue;
            }
            GoToMarker(entry.Key);
            LsProto.LinkedEditingRanges? actualRanges = LS().ProvideLinkedEditingRange(
                activeFilename,
                caretOffset());
            if (actualRanges != null)
            {
                throw new InvalidOperationException(
                    "Expected no linked editing ranges for marker '" + entry.Key + "' but got "
                    + actualRanges.Ranges.Length);
            }
        }
    }
}

#endif
