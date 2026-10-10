// Ported from tsgo internal/fourslash/fourslash.go — the harness core (:86-113, :294-343,
// :699-913, :986-1010, :3692-3975). The verify families live in Fourslash.Verify.cs and the
// baseline plumbing in Fourslash.Baseline.cs, mirroring the Go file's section order.
//
// ARCHITECTURE NOTE (the one sanctioned seam): Go drives a real LSP server over an in-memory
// channel (lsptestutil.NewLSPClient) and edits flow through textDocument/didChange. The C#
// foundation's equivalent of that server layer is Ts.Project.ProjectService (script-info
// registry → inferred/configured project → Program → LanguageService), so New() builds a
// ProjectService and every editScript() call maps to ChangeDocument/OpenDocument. Requests are
// made directly against the LanguageService — the JSON-RPC wire layer adds no observable
// behavior for the ported verifies. plan/08 §7.3 sanctions the Runtime/ merge into the test
// assembly; Ts.TestUtil keeps no Ts.LanguageService reference (the runner partials are linked
// into the test project under TS_FOURSLASH_RUNNER).
//
// PORT-NOTES:
//   - Go computes marker LS positions in UTF-8 converters; positions in this port are UTF-16
//     code units natively (plan/01 §UTF-16). All LSP conversions go through one Converters
//     instance over the session's script infos, exactly like Go's f.converters.
//   - `t.Fatalf` → exception (carries the Go message text); gotest.tools assert.Equal →
//     xunit Assert.Equal with expected-first argument order (plan/05 §16.3).
//   - Post-keystroke / post-paste formatting requests and the workspace/configuration exchange
//     have no C# counterpart yet; the stateEnableFormatting gate is kept and read where a
//     formatting request would have fired (the requests themselves are omitted — see the
//     omission table in the session report / plan/11).
//   - harnessutil.SetOptionsFromTestConfig copies options structurally in Go. The C# merge
//     feeds the directive values through the real command-line parser and copies a fixed field
//     list (CompilerOptions has no AOT-safe generic copy); fields outside the list are an
//     explicit port seam recorded in plan/11.

#if TS_FOURSLASH_RUNNER

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Ts.Core;
using Ts.Ls;
using Ts.Ls.LsConv;
using Ts.Vfs;
using Ts.Vfs.VfsTest;

namespace Ts.TestUtil.Fourslash;

public sealed partial class FourslashTest : IDisposable
{
    // fourslash.go:127 — rootDir.
    private const string RootDirectory = "/";

    private readonly Ts.Project.ProjectService projectService;

    // Go: f.vfs (fourslash.go:104) — the wrapped MapFS (with bundled:///). It is what the
    // baseline producers walk (baselineutil.go:588-600) and what a file's text is read back
    // from (textOfFile, :654). The C# ProjectService keeps its own copy and does not expose it,
    // so the harness carries the same instance.
    private readonly IFileSystem vfs;

    private readonly FourslashProject testData; // !!! consolidate test files from test data and script info
    private readonly FourslashBaselines baselines = new();
    private Dictionary<string, List<RangeMarker>>? rangesByText;

    // fourslash.go:52-53 — openFiles (map[string]struct{}) doubles as ensureActiveFile's didOpen
    // bookkeeping; insertion order is the open order, so both callers use Add/Remove/Contains.
    private readonly HashSet<string> openFiles = new(StringComparer.Ordinal);

    private readonly List<Marker> markers;
    private readonly List<RangeMarker> ranges;
    private readonly Dictionary<string, Marker> markerPositions;
    private readonly Dictionary<string, FourslashScriptInfo> scriptInfos;
    private readonly Converters converters;

    private bool stateEnableFormatting = true;
    private UserPreferences userPreferences = UserPreferences.NewDefaultUserPreferences();

    // fourslash.go:65 — capabilities. New() stamps the default capability document into it
    // (GetDefaultCapabilities); the LS entries that read client capabilities consume it through
    // LS() (ResolvedCapabilities).
    private LsProto.ClientCapabilities? capabilities;
    private LsProto.ResolvedClientCapabilities? resolvedCapabilities;

    // fourslash.go:2562 — MarkTestAsStradaServer.
    private bool isStradaServer;

    // fourslash.go:58 — reportFormatOnTypeCrash. Go's typeText clears the *field* to disable the
    // crash report while typing, but its only reader is the on-type-formatting request, which
    // this port omits (no C# formatting host); typeText therefore keeps Go's shadowing local
    // declaration only, and no instance field is needed.

    // fourslash.go:294-315 — getBaseFileNameFromTest. Go recovers it from the test method name;
    // the emitted corpus already passes exactly that name as `fileName` to New(), so it is
    // carried from there.
    private readonly string testBaseName;

    private LsProto.Position currentCaretPosition = new();
    private string? lastKnownMarkerName;
    private string activeFilename;
    private LsProto.Position? selectionEnd;

    // Semantic token configuration (fourslash.go:374-375). Mutable: New() lifts the values out
    // of FourslashOptions (Go's initialize() stamps the client capabilities into the server).
    private string[] semanticTokenTypes = DefaultSemanticTokenTypes();
    private string[] semanticTokenModifiers = DefaultSemanticTokenModifiers();

    private readonly CancellationToken cancellationToken;

    private FourslashTest(
        Ts.Project.ProjectService projectService,
        IFileSystem vfs,
        FourslashProject testData,
        List<Marker> markers,
        List<RangeMarker> ranges,
        Dictionary<string, Marker> markerPositions,
        Dictionary<string, FourslashScriptInfo> scriptInfos,
        Converters converters,
        string activeFilename,
        string testBaseName,
        CancellationToken cancellationToken)
    {
        this.projectService = projectService;
        this.vfs = vfs;
        this.testData = testData;
        this.markers = markers;
        this.ranges = ranges;
        this.markerPositions = markerPositions;
        this.scriptInfos = scriptInfos;
        this.converters = converters;
        this.activeFilename = activeFilename;
        this.testBaseName = testBaseName;
        this.cancellationToken = cancellationToken;
    }

    // fourslash.go:133-237 — NewFourslash. The Go `capabilities` parameter is threaded
    // (fourslash.go:472-638): it reaches the server through initialize(), which stamps the
    // client-provided semantic-token types/modifiers and the completion snippet/insert-replace
    // flags into the server's capability state. The C# foundation negotiates nothing — the raw
    // LS entry points take those parameters explicitly (ProvideSemanticTokens takes the
    // resolved capability object; ProvideCompletions reads preferences, not capabilities) — so
    // FourslashOptions is accepted and carried for shape parity, and the semantic-token
    // configuration below is the part that actually feeds a request.
    public sealed class FourslashOptions
    {
        public LsProto.ClientCapabilities? Capabilities;

        // fourslash.go:566-638 — getCapabilitiesWithDefaults (the pieces the C# LS reads).
        public string[]? SemanticTokenTypes;
        public string[]? SemanticTokenModifiers;
    }

    public static FourslashTest New(
        string content,
        string? fileName = null,
        FourslashOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!global::Ts.Bundled.Bundled.Embedded)
        {
            // Without embedding, we'd need to read all of the lib files out from disk into the MapFS.
            // Just skip this for now.
            throw new InvalidOperationException("bundled files are not embedded");
        }

        // Go: getBaseFileNameFromTest(t) + tspath.ExtensionTs — the test supplies the base name.
        string testBaseName = fileName ?? "test";
        string baseFileName = testBaseName + ".ts";

        var testfs = new Dictionary<string, object>(StringComparer.Ordinal);
        var scriptInfos = new Dictionary<string, FourslashScriptInfo>(StringComparer.Ordinal);
        FourslashProject testData = FourslashParser.ParseTestData(content, baseFileName);
        foreach (FourslashFile file in testData.Files)
        {
            string filePath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(file.VirtualFileName, RootDirectory);
            // Dynamic files (e.g., untitled:) shouldn't be added to the VFS
            if (!global::Ts.TsPath.TsPath.IsDynamicFileName(filePath))
            {
                testfs[filePath] = file.Content;
            }
            scriptInfos[filePath] = FourslashScriptInfo.New(filePath, file.Content);
        }

        foreach (KeyValuePair<string, string> link in testData.Symlinks)
        {
            string filePath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(link.Key, RootDirectory);
            testfs[filePath] = VfsTestUtil.Symlink(global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(link.Value, RootDirectory));
        }

        // !!! use default compiler options for inferred project as base
        var compilerOptions = new CompilerOptions
        {
            SkipDefaultLibCheck = Tristate.True,
            Target = ScriptTarget.LatestStandard,
            Jsx = JsxEmit.Preserve,
        };
        var harnessOptions = new Ts.TestUtil.HarnessOptions
        {
            UseCaseSensitiveFileNames = true,
            CurrentDirectory = RootDirectory,
        };
        Ts.TestUtil.HarnessUtil.SetOptionsFromTestConfig(
            testData.GlobalOptions,
            compilerOptions,
            harnessOptions,
            RootDirectory,
            allowUnknownOptions: true);

        // fourslash.go:173 — harnessutil.SkipUnsupportedCompilerOptions.
        Ts.TestUtil.HarnessUtil.SkipUnsupportedCompilerOptions(compilerOptions);

        // fourslash.go:175-176 — fsFromMap feeds vfstest.FromMap with the harness's
        // UseCaseSensitiveFileNames; fs is the bundled:///-wrapped view (that is what Go hands
        // the server, and what f.vfs is — the baseline producers read file text back through it).
        IFileSystem fsFromMap = VfsTestUtil.FromMap(testfs, harnessOptions.UseCaseSensitiveFileNames);
        IFileSystem fs = global::Ts.Bundled.Bundled.WrapFS(fsFromMap);

        var projectService = new Ts.Project.ProjectService(
            fs,
            RootDirectory,
            global::Ts.Bundled.Bundled.LibPath(),
            watcher: null,
            compilerOptions);

        // fourslash.go:145 — every path handed to the session is normalized against rootDir
        // (markers/ranges carry the same normalized names; see the parser).
        var normalizedFiles = new List<FourslashFile>(testData.Files.Count);
        foreach (FourslashFile file in testData.Files)
        {
            normalizedFiles.Add(file with
            {
                VirtualFileName = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(file.VirtualFileName, RootDirectory),
            });
        }
        testData = new FourslashProject(
            normalizedFiles,
            testData.MarkerPositions,
            testData.Markers,
            testData.Ranges,
            testData.NamedRanges,
            testData.GlobalOptions,
            testData.Symlinks);

        var converters = new Converters("utf-16", fileNameIn =>
            scriptInfos.TryGetValue(fileNameIn, out FourslashScriptInfo? scriptInfo)
                ? scriptInfo.LineMap
                : LineMap.ComputeLSPLineStarts(""));

        var (markers, ranges, markerPositions) = FourslashMarkers.FromProject(
            testData,
            fileNameIn => getFileTextFromVfs(fs, fileNameIn));
        var f = new FourslashTest(
            projectService,
            fs,
            testData,
            markers,
            ranges,
            markerPositions,
            scriptInfos,
            converters,
            activeFilename: testData.Files[0].VirtualFileName,
            testBaseName,
            cancellationToken);

        // fourslash.go:472-556 — GetDefaultCapabilities is what the corpus passes when it needs
        // the default client capabilities; the pieces the C# LS actually reads (semantic-token
        // types/modifiers) are lifted from the options here. The raw LS entry points take the
        // completion/interaction flags explicitly, so Capabilities itself is carried, not
        // negotiated.
        f.capabilities = options?.Capabilities ?? GetDefaultCapabilities();
        if (options?.SemanticTokenTypes is { Length: > 0 } tokenTypes)
        {
            f.semanticTokenTypes = tokenTypes;
        }
        if (options?.SemanticTokenModifiers is { Length: > 0 } tokenModifiers)
        {
            f.semanticTokenModifiers = tokenModifiers;
        }

        if (testData.IsStateBaseliningEnabled)
        {
            // fourslash.go:228-235 — the state baseline (statebaseline.go) rides the project
            // snapshot/collection surface, which the C# foundation does not expose. The corpus
            // shape that sets `// @statebaseline` is rejected loudly (recorded seam; the
            // affected tests are listed in Scripts/csharpFailingTests.txt).
            throw new NotSupportedException(
                "statebaseline is not supported by the C# fourslash port yet (project snapshot surface not ported).");
        }

        foreach (FourslashFile file in testData.Files)
        {
            f.openFile(file.VirtualFileName);
        }
        f.activeFilename = f.testData.Files[0].VirtualFileName;

        return f;
    }

    // fourslash.go:236-239 — the deferred closeClient()+verifyBaselines() pair. The generated
    // tests hold the harness in `using var f`, so Dispose is that deferred call.
    public void Dispose()
    {
        projectService.Dispose();
        if (!testData.IsStateBaseliningEnabled)
        {
            // fourslash.go:5041-5049 — verifyBaselines(testPath). Go recovers the test path from
            // runtime.Caller; here the basename corpus's baseline key comes from the emitted
            // fileName (FourslashBaselineUtil.GetBaselineFileName).
            verifyBaselines(testBaseName);
        }
    }

    // fourslash.go:986-1009 — getLanguageKind. (Extensions spelled out; they are the Go
    // tspath.Extension* constants.)
    private static string GetLanguageKind(string filename)
    {
        if (global::Ts.TsPath.TsPath.FileExtensionIsOneOf(
            filename,
            [".ts", ".mts", ".cts", ".d.mts", ".d.cts", ".d.ts"]))
        {
            return "typescript";
        }
        if (global::Ts.TsPath.TsPath.FileExtensionIsOneOf(filename, [".js", ".mjs", ".cjs"]))
        {
            return "javascript";
        }
        if (global::Ts.TsPath.TsPath.FileExtensionIs(filename, ".jsx"))
        {
            return "javascriptreact";
        }
        if (global::Ts.TsPath.TsPath.FileExtensionIs(filename, ".tsx"))
        {
            return "typescriptreact";
        }
        if (global::Ts.TsPath.TsPath.FileExtensionIs(filename, ".json"))
        {
            return "json";
        }
        return "typescript"; // !!! should we error in this case?
    }

    // fourslash.go:344-370 — defaultSemanticTokenTypes.
    private static string[] DefaultSemanticTokenTypes() =>
    [
        "namespace", "class", "enum", "interface", "struct", "typeParameter", "type",
        "parameter", "variable", "property", "enumMember", "decorator", "event",
        "function", "method", "macro", "label", "comment", "string", "keyword",
        "number", "regexp", "operator",
    ];

    // fourslash.go:372-396 — defaultSemanticTokenModifiers.
    private static string[] DefaultSemanticTokenModifiers() =>
    [
        "declaration", "definition", "readonly", "static", "deprecated", "abstract",
        "async", "modification", "documentation", "defaultLibrary", "local",
    ];

    // fourslash.go:893-912 — openFile (didOpen → ProjectService.OpenDocument).
    private void openFile(string filename)
    {
        FourslashScriptInfo? script = getScriptInfo(filename);
        if (script == null)
        {
            // fourslash.go:895-901 — Go falls back to reading the file straight from the VFS
            // (which is the bundled:///-wrapped view, so a request for a lib file resolves to
            // its embedded text) and registers the script info it just created. Only when that
            // read fails too does it Fatalf.
            string? content = ReadTextFileOrNull(vfs, filename);
            if (content != null)
            {
                script = FourslashScriptInfo.New(filename, content);
                scriptInfos[filename] = script;
            }
            else
            {
                throw new InvalidOperationException("File " + filename + " not found in test data");
            }
        }
        activeFilename = filename;
        projectService.OpenDocument(filename, script.Content, script.Version);
        openFiles.Add(filename);
    }

    // fourslash.go:899 — f.vfs.ReadFile(filename) (contents string, ok bool). The C# IFileSystem
    // spells the same shape TryReadFile(path, out contents); a missing file is the `ok == false`
    // arm.
    private static string? ReadTextFileOrNull(IFileSystem fs, string fileName) =>
        fs.TryReadFile(fileName, out string contents) ? contents : null;

    // fourslash.go:818-830 — GoToFile / GoToFileNumber.
    public void GoToFile(string filename)
    {
        filename = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(filename, RootDirectory);
        openFile(filename);
    }

    public void GoToFileNumber(int index)
    {
        if ((uint)index >= (uint)testData.Files.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Invalid file index: " + index);
        }
        openFile(testData.Files[index].VirtualFileName);
    }

    // fourslash.go:831-861 — Markers / MarkerNames / MarkerByName / Ranges.
    public List<Marker> Markers() => markers;

    public List<string> MarkerNames() => markerPositions.Keys.ToList();

    public Marker MarkerByName(string name)
    {
        if (markerPositions.TryGetValue(name, out Marker? marker))
        {
            return marker;
        }
        throw new KeyNotFoundException("Marker not found: " + name);
    }

    public List<RangeMarker> Ranges() => ranges;

    // fourslash.go:852-861 — getRangesInFile.
    public List<RangeMarker> getRangesInFile(string fileName) =>
        ranges.Where(range => range.FileName == fileName).ToList();

    // fourslash.go:862-870 — ensureActiveFile. Go only reopens when the file is not already in
    // openFiles ({ didOpen } doubles as the open set).
    private void ensureActiveFile(string filename)
    {
        if (filename != activeFilename)
        {
            if (!openFiles.Contains(filename))
            {
                openFile(filename);
            }
            else
            {
                activeFilename = filename;
            }
        }
    }

    // fourslash.go:872-892 — CloseFileOfMarker (didClose; kept for state parity — unused by
    // the ported verifies).
    public void CloseFileOfMarker(string markerName)
    {
        Marker marker = MarkerByName(markerName);
        string filename = marker.FileName;
        projectService.CloseDocument(filename);
        openFiles.Remove(filename);
    }

    // fourslash.go:723-817 — the GoTo family.
    public void GoToMarkerOrRange(IFourslashMarkerOrRange markerOrRange)
    {
        goToMarker(markerOrRange);
    }

    public void GoToMarker(string markerName)
    {
        goToMarker(MarkerByName(markerName));
    }

    private void goToMarker(IFourslashMarkerOrRange markerOrRange)
    {
        ensureActiveFile(markerOrRange.FileName);
        lastKnownMarkerName = markerOrRange.GetName();
        goToPosition(markerOrRange.LSPos());
    }

    public void GoToEOF()
    {
        FourslashScriptInfo script = getScriptInfo(activeFilename)!;
        goToPosition(converters.PositionToLineAndCharacter(script, script.Content.Length));
    }

    public void GoToBOF()
    {
        goToPosition(new LsProto.Position());
    }

    public void GoToPosition(int position)
    {
        goToPosition(converters.PositionToLineAndCharacter(getScriptInfo(activeFilename)!, position));
    }

    private void goToPosition(LsProto.Position position)
    {
        currentCaretPosition = position;
        selectionEnd = null;
    }

    public void GoToEachMarker(List<string> markerNames, Action<Marker, int> action)
    {
        for (int i = 0; i < markerNames.Count; i++)
        {
            Marker marker = MarkerByName(markerNames[i]);
            goToMarker(marker);
            action(marker, i);
        }
    }

    public void GoToEachRange(Action<RangeMarker> action)
    {
        foreach (RangeMarker rangeMarker in ranges)
        {
            goToRangeStart(rangeMarker);
            action(rangeMarker);
        }
    }

    public void GoToRangeStart(RangeMarker rangeMarker)
    {
        goToRangeStart(rangeMarker);
    }

    private void goToRangeStart(RangeMarker rangeMarker)
    {
        ensureActiveFile(rangeMarker.FileName);
        goToPosition(rangeMarker.LSRange.Start);
    }

    public void GoToSelect(string startMarkerName, string endMarkerName)
    {
        goToMarker(MarkerByName(startMarkerName));
        Marker endMarker = MarkerByName(endMarkerName);
        selectionEnd = endMarker.LSPosition;
    }

    public void GoToSelectRange(RangeMarker rangeMarker)
    {
        ensureActiveFile(rangeMarker.FileName);
        goToPosition(rangeMarker.LSRange.Start);
        selectionEnd = rangeMarker.LSRange.End;
    }

    // fourslash.go:1208-1219 — goToMarkerInput. Go's switch has exactly two live arms
    // (string, *Marker) and falls through to Fatalf on anything else — including a nil
    // MarkerInput, which is what Go receives when a caller passes an untyped nil. The corpus
    // reaches this helper only through VerifyJSDocCompletion/VerifyNoJSDocCompletion, whose
    // argument is a string or a *Marker; the arms are spelled to match Go's (a null input
    // therefore throws here too, unlike the earlier permissive C# version).
    private void goToMarkerInput(object? markerInput)
    {
        switch (markerInput)
        {
            case string markerName:
                GoToMarker(markerName);
                break;
            case Marker marker:
                goToMarker(marker);
                break;
            default:
                throw new InvalidOperationException(
                    "Invalid marker input type: " + (markerInput?.GetType().Name ?? "<nil>")
                    + ". Expected string or Marker.");
        }
    }

    // fourslash.go:5023-5039 — GetRangesByText / getRangeText. Go returns a MultiMap indexed by
    // the range's text; the C# corpus reads it as either an element list (`[.. f.GetRangesByText()[k]]`)
    // or a single element (`f.GetRangesByText()[k][0]`), exactly Go's slicing of the MultiMap's
    // []*RangeMarker value.
    public Dictionary<string, List<RangeMarker>> GetRangesByText()
    {
        if (rangesByText != null)
        {
            return rangesByText;
        }
        var newRangesByText = new Dictionary<string, List<RangeMarker>>(StringComparer.Ordinal);
        foreach (RangeMarker r in ranges)
        {
            string rangeText = getRangeText(r);
            if (!newRangesByText.TryGetValue(rangeText, out List<RangeMarker>? bucket))
            {
                bucket = new List<RangeMarker>();
                newRangesByText[rangeText] = bucket;
            }
            bucket.Add(r);
        }
        rangesByText = newRangesByText;
        return newRangesByText;
    }

    // The single-element view of GetRangesByText() the corpus also spells is plain indexing
    // (Go: multiMap.Get(key)[0]) — no helper needed.

    private string getRangeText(RangeMarker r)
    {
        FourslashScriptInfo script = getScriptInfo(r.FileName)!;
        return script.Content[r.Range.Pos..r.Range.End];
    }

    // fourslash.go:3692-3703 — Insert / InsertLine. (baselineState is a no-op here: the state
    // baseline port is omitted — see New().)
    public void Insert(string text)
    {
        typeText(text);
    }

    // Insert text and a new line at the current caret position.
    public void InsertLine(string text)
    {
        typeText(text + "\n");
    }

    // fourslash.go:3705-3719 — Backspace.
    public void Backspace(int count)
    {
        FourslashScriptInfo script = getScriptInfo(activeFilename)!;
        int offset = converters.LineAndCharacterToPosition(script, currentCaretPosition);

        for (int i = 0; i < count; i++)
        {
            offset--;
            editScriptAndUpdateMarkers(activeFilename, offset, offset + 1, "");
            currentCaretPosition = converters.PositionToLineAndCharacter(script, offset);
            // Don't need to examine formatting because there are no formatting changes on backspace.
        }

        // f.checkPostEditInvariants() // !!! do we need this?
    }

    // fourslash.go:3721-3731 — DeleteAtCaret.
    public void DeleteAtCaret(int count)
    {
        FourslashScriptInfo script = getScriptInfo(activeFilename)!;
        int offset = converters.LineAndCharacterToPosition(script, currentCaretPosition);

        for (int i = 0; i < count; i++)
        {
            editScriptAndUpdateMarkers(activeFilename, offset, offset + 1, "");
            // Position stays the same after delete (unlike backspace)
        }
    }

    // fourslash.go:3733-3757 — Paste. (Post-paste range formatting omitted: the formatting LS
    // entry is not ported; the edit itself is faithful.)
    public void Paste(string text)
    {
        FourslashScriptInfo script = getScriptInfo(activeFilename)!;
        int start = converters.LineAndCharacterToPosition(script, currentCaretPosition);
        editScriptAndUpdateMarkers(activeFilename, start, start, text);

        // post-paste formatting
        if (stateEnableFormatting)
        {
            // OMITTED: textDocument/rangeFormatting entry not ported (no C# formatting host).
        }
    }

    // fourslash.go:3759-3764 — ReplaceLine.
    public void ReplaceLine(int lineIndex, string text)
    {
        selectLine(lineIndex);
        typeText(text);
    }

    // fourslash.go:3766-3776 — selectLine.
    private void selectLine(int lineIndex)
    {
        FourslashScriptInfo script = getScriptInfo(activeFilename)!;
        int start = script.LineMap.LineStarts[lineIndex];
        int end;
        if (lineIndex + 1 >= script.LineMap.LineStarts.Count)
        {
            end = script.Content.Length;
        }
        else
        {
            end = script.LineMap.LineStarts[lineIndex + 1] - 1;
        }
        selectRange(new TextRange(start, end));
    }

    // fourslash.go:3778-3784 — selectRange.
    private void selectRange(TextRange textRange)
    {
        FourslashScriptInfo script = getScriptInfo(activeFilename)!;
        LsProto.Position start = converters.PositionToLineAndCharacter(script, textRange.Pos);
        LsProto.Position end = converters.PositionToLineAndCharacter(script, textRange.End);
        goToPosition(start);
        selectionEnd = end;
    }

    // fourslash.go:3786-3798 — getSelection.
    private TextRange getSelection()
    {
        FourslashScriptInfo script = getScriptInfo(activeFilename)!;
        if (selectionEnd == null)
        {
            int caret = converters.LineAndCharacterToPosition(script, currentCaretPosition);
            return new TextRange(caret, caret);
        }
        return new TextRange(
            converters.LineAndCharacterToPosition(script, currentCaretPosition),
            converters.LineAndCharacterToPosition(script, selectionEnd!));
    }

    // fourslash.go:3800-3832 — applyTextEdits (updates f.currentCaretPosition).
    private int applyTextEdits(IReadOnlyList<LsProto.TextEdit> edits)
    {
        FourslashScriptInfo script = getScriptInfo(activeFilename)!;
        List<LsProto.TextEdit> sorted = edits.OrderBy(
            edit => converters.LineAndCharacterToPosition(script, edit.Range.Start)).ToList();

        int totalOffset = 0;
        int caret = converters.LineAndCharacterToPosition(script, currentCaretPosition);
        // Apply edits in reverse order to avoid affecting the positions of earlier edits.
        for (int i = sorted.Count - 1; i >= 0; i--)
        {
            LsProto.TextEdit edit = sorted[i];
            int start = converters.LineAndCharacterToPosition(script, edit.Range.Start);
            int end = converters.LineAndCharacterToPosition(script, edit.Range.End);
            editScriptAndUpdateMarkers(activeFilename, start, end, edit.NewText);

            int delta = edit.NewText.Length - (end - start);
            if (start <= caret)
            {
                if (end <= caret)
                {
                    // The entirety of the edit span falls before the caret position, shift the caret accordingly
                    caret += delta;
                }
                else
                {
                    // The span being replaced includes the caret position, place the caret at the beginning of the span
                    caret = start;
                }
            }
            totalOffset += delta;
        }
        currentCaretPosition = converters.PositionToLineAndCharacter(script, caret);
        return totalOffset;
    }

    // fourslash.go:3834-3843 — Replace / replaceWorker.
    public void Replace(int start, int length, string text)
    {
        replaceWorker(start, length, text);
    }

    private void replaceWorker(int start, int length, string text)
    {
        editScriptAndUpdateMarkers(activeFilename, start, start + length, text);
        // f.checkPostEditInvariants() // !!! do we need this?
    }

    // fourslash.go:3845-3885 — typeText. (The on-type-formatting request inside the loop is
    // omitted — no C# formatting entry; the character-by-character edit walk is faithful. Go
    // types whole runes; UTF-16 surrogate pairs move as one step for the same reason.)
    private void typeText(string text)
    {
        // temprorary -- this disables tests failing if format crashes; this unblocks unrelated tests such as codefixes
        bool reportFormatOnTypeCrash = false;

        FourslashScriptInfo script = getScriptInfo(activeFilename)!;
        TextRange selection = getSelection();
        replaceWorker(selection.Pos, selection.End - selection.Pos, "");

        int totalSize = 0;

        int offset = converters.LineAndCharacterToPosition(script, currentCaretPosition);
        while (totalSize < text.Length)
        {
            string r = char.ConvertFromUtf32(char.ConvertToUtf32(text, totalSize));
            int size = r.Length;
            editScriptAndUpdateMarkers(activeFilename, offset, offset, r);

            totalSize += size;
            offset += size;
            currentCaretPosition = converters.PositionToLineAndCharacter(script, offset);

            // Handle post-keystroke formatting
            if (stateEnableFormatting)
            {
                // OMITTED: textDocument/onTypeFormatting entry not ported (no C# formatting
                // host); the reportFormatOnTypeCrash flag lives only in this scope until it
                // lands.
                _ = reportFormatOnTypeCrash;
            }
        }

        // f.checkPostEditInvariants() // !!! do we need this?
    }

    // fourslash.go:3887-3922 — editScriptAndUpdateMarkers(Worker).
    private void editScriptAndUpdateMarkers(string fileName, int editStart, int editEnd, string newText)
    {
        editScriptAndUpdateMarkersWorker(fileName, [new TextChange(new TextRange(editStart, editEnd), newText)]);
    }

    private void editScriptAndUpdateMarkersWorker(string fileName, IReadOnlyList<TextChange> changes)
    {
        // Sort changes by position (ascending) so we can apply in reverse
        List<TextChange> sortedChanges = changes.OrderBy(static change => change.Pos).ToList();

        // Apply changes in reverse order to preserve positions of earlier changes
        for (int i = sortedChanges.Count - 1; i >= 0; i--)
        {
            TextChange change = sortedChanges[i];
            int editStart = change.Pos;
            int editEnd = change.End;
            FourslashScriptInfo script = editScript(fileName, change);
            foreach (Marker marker in markers)
            {
                if (marker.FileName == fileName)
                {
                    marker.Position = UpdatePosition(marker.Position, editStart, editEnd, change.NewText);
                    marker.LSPosition = converters.PositionToLineAndCharacter(script, marker.Position);
                }
            }
            foreach (RangeMarker rangeMarker in ranges)
            {
                if (rangeMarker.FileName == fileName)
                {
                    int start = UpdatePosition(rangeMarker.Range.Pos, editStart, editEnd, change.NewText);
                    int end = UpdatePosition(rangeMarker.Range.End, editStart, editEnd, change.NewText);
                    rangeMarker.Range = new TextRange(start, end);
                    rangeMarker.LSRange = converters.ToLSPRange(script, rangeMarker.Range);
                }
            }
        }
        rangesByText = null;
    }

    // fourslash.go:3924-3933 — updatePosition.
    private static int UpdatePosition(int pos, int editStart, int editEnd, string newText)
    {
        if (pos <= editStart)
        {
            return pos;
        }
        // If inside the edit, return -1 to mark as invalid
        if (pos < editEnd)
        {
            return -1;
        }
        return pos + newText.Length - (editEnd - editStart);
    }

    // fourslash.go:3935-3959 — editScript. (VFS WriteFile + didChange → ProjectService; a file
    // that was never opened gets OpenDocument first, which is what Go's didChange-after-load
    // effectively produces on the server.)
    private FourslashScriptInfo editScript(string fileName, TextChange change)
    {
        FourslashScriptInfo script = getOrLoadScriptInfo(fileName)
            ?? throw new InvalidOperationException("Script info for file " + fileName + " not found");

        script.EditContent(change);
        if (projectService.GetScriptInfo(fileName) == null)
        {
            projectService.OpenDocument(fileName, script.Content, script.Version);
        }
        else
        {
            projectService.ChangeDocument(fileName, script.Content, script.Version);
        }
        return script;
    }

    // fourslash.go:3961-3975 — getScriptInfo / getOrLoadScriptInfo.
    public FourslashScriptInfo? getScriptInfo(string fileName) =>
        scriptInfos.GetValueOrDefault(fileName);

    // The LanguageService for the active file, resolved per call: ProjectService rebuilds the
    // project (and its LanguageService) on every ChangeDocument, mirroring how Go's requests
    // are always answered from the server's current program state. The direct-program host has
    // no configuration exchange, so the preference set the session adopted (Configure /
    // ConfigureWithReset) and the client capabilities are installed on the service the request
    // will run against — Go's server reads both per request (host.GetPreferences /
    // lsproto.GetClientCapabilities).
    private Ts.Ls.LanguageService LS()
    {
        Ts.Ls.LanguageService ls = projectService.GetLanguageService(activeFilename);
        ls.ConfigureWithPreferences(userPreferences, ResolvedCapabilities());
        return ls;
    }

    // fourslash.go:557-636 + :100-131 — Go's initialize() runs the client document through
    // getCapabilitiesWithDefaults before the server resolves it. The C# harness must merge first:
    // the corpus mutates the document returned by GetDefaultCapabilities() *before* New() (e.g.
    // docCommentTemplate* clears CompletionItem.SnippetSupport), so the raw field is what the
    // session stored, and the merge supplies the named capabilities the test left unset (the
    // shipped reference hover baselines are markdown, which only Hover.ContentFormat unset +
    // default yields).
    private LsProto.ResolvedClientCapabilities ResolvedCapabilities() =>
        resolvedCapabilities ??=
            Ts.LsProto.ResolvedClientCapabilitiesExtensions.Resolve(WithDefaults(capabilities));

    private FourslashScriptInfo? getOrLoadScriptInfo(string fileName)
    {
        if (getScriptInfo(fileName) is { } script)
        {
            return script;
        }
        // Go reads the file back out of the VFS; the unopened test files are exactly the
        // parse-time content.
        foreach (FourslashFile file in testData.Files)
        {
            string filePath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(file.VirtualFileName, RootDirectory);
            if (filePath == fileName)
            {
                script = FourslashScriptInfo.New(fileName, file.Content);
                scriptInfos[fileName] = script;
                return script;
            }
        }
        return null;
    }

    // fourslash.go:699-701 — GetOptions.
    public UserPreferences GetOptions() => userPreferences;

    // fourslash.go:703-713 — Configure. Go sends workspace/didChangeConfiguration; the
    // direct-program host has no configuration exchange, so the copy is handed to the
    // LanguageService (see LS()) — the LS installs it in ConfigureWithPreferences before each
    // request, which is what "the server adopted the new config" means here.
    public void Configure(UserPreferences config)
    {
        userPreferences = config;
    }

    // fourslash.go:715-721 — ConfigureWithReset. Go returns the reset closure; the reset
    // argument is the original config, so passing it back through Configure is the same call.
    public void ConfigureWithReset(UserPreferences config, out UserPreferences saved)
    {
        saved = userPreferences;
        Configure(config);
    }

    // The verify families' `if preferences != nil { reset := f.ConfigureWithReset(...); defer reset() }`
    // shape (fourslash.go:1590-1593 and friends). null means "no override", which is Go's nil.
    public UserPreferences? ConfigureWithPreferences(UserPreferences? preferences)
    {
        if (preferences == null)
        {
            return null;
        }
        UserPreferences saved = userPreferences;
        Configure(preferences);
        return saved;
    }

    public void RestorePreferences(UserPreferences? saved)
    {
        if (saved != null)
        {
            Configure(saved);
        }
    }

    // The C# shape of the generator's format.setOption lowering (Go: GetOptions / field assign /
    // Configure — see convertFourslash-csharp.mts parseFormatStatement). The lambda receives a
    // clone of the session's FormatCodeSettings, so the single field assignment the corpus
    // spells is applied to a copy exactly like Go's value-struct copy of the options.
    public void ConfigureFormat(Action<global::Ts.Format.FormatCodeSettings> configure)
    {
        UserPreferences options = userPreferences.Clone();
        options.FormatCodeSettings = options.FormatCodeSettings.Clone();
        configure(options.FormatCodeSettings);
        Configure(options);
    }

    // fourslash.go:2015-2029 — applyEditsToContent (the non-mutating twin of applyTextEdits):
    // sort by start position, then splice from the back. Positions are UTF-16 code units, and
    // each edit's range is resolved against the original content (all edits share the file).
    private string applyEditsToContent(string content, IReadOnlyList<LsProto.TextEdit> edits)
    {
        FourslashScriptInfo script = getScriptInfo(activeFilename)!;
        var sorted = new List<LsProto.TextEdit>(edits);
        sorted.Sort((a, b) => converters.LineAndCharacterToPosition(script, a.Range.Start)
            - converters.LineAndCharacterToPosition(script, b.Range.Start));
        for (int i = sorted.Count - 1; i >= 0; i--)
        {
            LsProto.TextEdit edit = sorted[i];
            int start = converters.LineAndCharacterToPosition(script, edit.Range.Start);
            int end = converters.LineAndCharacterToPosition(script, edit.Range.End);
            content = string.Concat(content.AsSpan(0, start), edit.NewText, content.AsSpan(end));
        }
        return content;
    }

    // fourslash.go:2562-2564 — MarkTestAsStradaServer.
    public void MarkTestAsStradaServer()
    {
        isStradaServer = true;
    }

    // fourslash.go:914-928 — FormatDocument.
    public void FormatDocument(string filename)
    {
        if (filename.Length == 0)
        {
            filename = activeFilename;
        }
        LsProto.TextEditsOrNull? edits = LS().ProvideFormatDocument(
            new global::Ts.Core.RequestContext { Cancellation = cancellationToken },
            global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(filename),
            LsFormatOptions());
        if (edits?.TextEdits is { } textEdits)
        {
            applyTextEdits(textEdits);
        }
    }

    // fourslash.go:930-958 — FormatSelection.
    public void FormatSelection(string startMarkerName, string endMarkerName)
    {
        Marker startMarker = MarkerByName(startMarkerName);
        Marker endMarker = MarkerByName(endMarkerName);
        if (startMarker.FileName != endMarker.FileName)
        {
            throw new InvalidOperationException(
                "Markers '" + startMarkerName + "' and '" + endMarkerName + "' are in different files");
        }
        string filename = startMarker.FileName;
        LsProto.TextEditsOrNull? edits = LS().ProvideFormatDocumentRange(
            new global::Ts.Core.RequestContext { Cancellation = cancellationToken },
            global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(filename),
            LsFormatOptions(),
            new LsProto.Range { Start = startMarker.LSPosition, End = endMarker.LSPosition });
        if (edits?.TextEdits is { } textEdits)
        {
            applyTextEdits(textEdits);
        }
    }

    // userpreferences.go — f.userPreferences.FormatCodeSettings.ToLSFormatOptions()
    // (lsutil/formatcodeoptions.go:105-112): TabSize, InsertSpaces and TrimTrailingWhitespace
    // only. The remaining FormattingOptions members are Go zero values and are left unset.
    private LsProto.FormattingOptions LsFormatOptions()
    {
        global::Ts.Format.FormatCodeSettings settings = userPreferences.FormatCodeSettings;
        return new LsProto.FormattingOptions
        {
            TabSize = (uint)settings.TabSize,
            InsertSpaces = settings.ConvertTabsToSpaces.IsTrue(),
            TrimTrailingWhitespace = settings.TrimTrailingWhitespace.IsTrue(),
        };
    }

    // fourslash.go:614-626 — textOfFile. Go walks f.vfs (the bundled-wrapped map fs the session
    // was built over); ReadAll/ReadFile surface here as TryReadFile.
    public bool textOfFile(string fileName, out string content)
    {
        if (vfs.TryReadFile(fileName, out string? text))
        {
            content = text;
            return true;
        }
        content = "";
        return false;
    }

    // fourslash.go:614-626 — textOfFile (throwing form; the Go name is reused by the baseline
    // producers, which are the only C# callers that want the failure to surface as a test error).
    private string textOfFileOrThrow(string fileName)
    {
        if (!textOfFile(fileName, out string content))
        {
            throw new InvalidOperationException("Could not find file " + fileName + " in the vfs");
        }
        return content;
    }

    // harnessutil.TestFile read-back for an arbitrary VFS path (Go's f.vfs.ReadFile) — used by
    // New() while the session (and therefore textOfFile) does not exist yet.
    private static string getFileTextFromVfs(IFileSystem fileSystem, string fileName) =>
        fileSystem.TryReadFile(fileName, out string? text) ? text : "";
}

// fourslash.go:67-101 — scriptInfo.
public sealed class FourslashScriptInfo : Script
{
    private FourslashScriptInfo(string fileName, string content, LSPLineMap lineMap, int version)
    {
        FileName = fileName;
        Content = content;
        LineMap = lineMap;
        Version = version;
    }

    public static FourslashScriptInfo New(string fileName, string content) =>
        new(fileName, content, global::Ts.Ls.LsConv.LineMap.ComputeLSPLineStarts(content), 1);

    public string FileName { get; }

    public string Content { get; private set; }

    public LSPLineMap LineMap { get; private set; }

    public int Version { get; private set; }

    // fourslash.go:78-82 — editContent.
    public void EditContent(TextChange change)
    {
        Content = change.ApplyTo(Content);
        LineMap = global::Ts.Ls.LsConv.LineMap.ComputeLSPLineStarts(Content);
        Version++;
    }

    // fourslash.go:92-101 — GetLineContent.
    public string GetLineContent(int line)
    {
        int numLines = LineMap.LineStarts.Count;
        if (line < 0 || line >= numLines)
        {
            return "";
        }
        int start = LineMap.LineStarts[line];
        int end = line + 1 < numLines ? LineMap.LineStarts[line + 1] : Content.Length;

        return Content[start..end].TrimEnd('\r', '\n');
    }

    string Script.FileName() => FileName;

    string Script.Text() => Content;
}

#endif
