// Ported from tsgo internal/compiler/program.go (diagnostics aggregation region, lines ~408-1473, 1755-1791).
//
// SCOPE (CLI type-check-and-report wave, phase 7 subset): this brings the diagnostics-collection
// orchestration online so that tscs can gather syntactic + bind + semantic + global diagnostics for a
// built Program and hand them to the diagnosticwriter. The parallel-collection helpers, the checker
// dispatch, @ts-ignore/@ts-expect-error directive filtering, and SortAndDeduplicateDiagnostics are
// ported real.
//
// Deferred subsurfaces (documented with PORT-NOTE, not reached by a legal type-check-and-report run):
//   * GetDeclarationDiagnostics / GetSuggestionDiagnostics — emit-host / suggestion surfaces (phase 6).
//   * The plain-JS diagnostic-code filter (plainJSErrors, ~100 codes) — the checker's JS surface is
//     being filled in parallel; the plain-JS branch is stubbed behind IsPlainJSFile.
//
// §1.5 (namespace shadowing): sibling-namespace static classes / types are fully qualified with global::.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Core;

namespace Ts.Compiler;

public sealed partial class Program
{
    // program.go:408-410
    public Diagnostic[] GetConfigFileParsingDiagnostics() =>
        opts.Config.GetConfigFileParsingDiagnostics() ?? [];

    // program.go:539-548 — collectDiagnostics collects diagnostics from a single file or all files.
    // If sourceFile is non-null, returns diagnostics for just that file.
    // If sourceFile is null, returns diagnostics for all files in the program.
    private Diagnostic[] CollectDiagnostics(CancellationToken ctx, SourceFile? sourceFile, bool concurrent, Func<CancellationToken, SourceFile, Diagnostic[]> collect)
    {
        Diagnostic[] result;
        if (sourceFile is not null)
        {
            result = collect(ctx, sourceFile);
        }
        else
        {
            var diagnostics = CollectDiagnosticsFromFiles(ctx, processed.Files, concurrent, collect);
            result = Concat(diagnostics);
        }
        return SortAndDeduplicateDiagnostics(result);
    }

    // program.go:550-560
    private Diagnostic[][] CollectDiagnosticsFromFiles(CancellationToken ctx, SourceFile[] sourceFiles, bool concurrent, Func<CancellationToken, SourceFile, Diagnostic[]> collect)
    {
        var diagnostics = new Diagnostic[sourceFiles.Length][];
        var wg = WorkGroup.Create(!concurrent || SingleThreaded());
        for (int i = 0; i < sourceFiles.Length; i++)
        {
            int idx = i;
            var file = sourceFiles[i];
            wg.Queue(() =>
            {
                diagnostics[idx] = collect(ctx, file);
            });
        }
        wg.RunAndWait();
        return diagnostics;
    }

    // program.go:567-578 — collectCheckerDiagnostics: same shape as collectDiagnostics but the callback
    // receives a checker for each file.
    private Diagnostic[] CollectCheckerDiagnostics(CancellationToken ctx, SourceFile? sourceFile, Func<CancellationToken, global::Ts.Checker.Checker, SourceFile, Diagnostic[]> collect)
    {
        if (sourceFile is not null)
        {
            if (SkipTypeChecking(sourceFile, false))
            {
                return [];
            }
            var (c, done) = GetTypeCheckerForFileExclusive(ctx, sourceFile);
            var result = collect(ctx, c, sourceFile);
            done();
            return SortAndDeduplicateDiagnostics(result);
        }
        return SortAndDeduplicateDiagnostics(Concat(CollectCheckerDiagnosticsFromFiles(ctx, processed.Files, collect)));
    }

    // program.go:581-602
    // program.go:583-604 — collectCheckerDiagnosticsFromFiles.
    private Diagnostic[][] CollectCheckerDiagnosticsFromFiles(CancellationToken ctx, SourceFile[] sourceFiles, Func<CancellationToken, global::Ts.Checker.Checker, SourceFile, Diagnostic[]> collect)
    {
        var diagnostics = new Diagnostic[sourceFiles.Length][];
        if (compilerCheckerPool is not null)
        {
            // Group by checker: one task per checker, each holding its checker's lock while it visits its
            // assigned files in order. This is load-bearing for correctness — the checker carries mutable
            // per-instance state (the typeResolutions stack, resolution caches), so two files pinned to the
            // same checker (index%N) must never be checked concurrently. The earlier per-file fallback
            // used GetCheckerForFileNonExclusive (no lock) and raced that state under 4-checker mode.
            compilerCheckerPool.ForEachCheckerGroupDo(ctx, sourceFiles, SingleThreaded(), (c, fileIndex, file) =>
            {
                diagnostics[fileIndex] = collect(ctx, c, file);
            });
        }
        else
        {
            var wg = WorkGroup.Create(SingleThreaded());
            for (int i = 0; i < sourceFiles.Length; i++)
            {
                int idx = i;
                var file = sourceFiles[i];
                if (SkipTypeChecking(file, false))
                {
                    continue;
                }
                wg.Queue(() =>
                {
                    var (c, done) = GetTypeCheckerForFile(ctx, file);
                    diagnostics[idx] = collect(ctx, c, file);
                    done();
                });
            }
            wg.RunAndWait();
        }
        return diagnostics;
    }

    // program.go:604-615
    public Diagnostic[] GetSyntacticDiagnostics(CancellationToken ctx, SourceFile? sourceFile) =>
        CollectDiagnostics(ctx, sourceFile, false /*concurrent*/, (_, file) =>
        {
            var diags = CoreUtil.Concatenate(file.Diagnostics(), file.JSDiagnostics()) ?? [];
            // PORT-NOTE: the option-dependent JS syntactic diagnostics (getAdditionalJSSyntacticDiagnostics,
            // program.go:610-612) apply only to unchecked JS files; deferred with the JS surface. A legal
            // .ts type-check-and-report run never reaches this branch.
            return diags;
        });

    // program.go:648-657
    public Diagnostic[] GetBindDiagnostics(CancellationToken ctx, SourceFile? sourceFile)
    {
        if (sourceFile is not null)
        {
            global::Ts.Binder.Binder.BindSourceFile(sourceFile);
        }
        else
        {
            BindSourceFiles();
        }
        return CollectDiagnostics(ctx, sourceFile, false /*concurrent*/, (_, file) => file.BindDiagnostics() ?? []);
    }

    // program.go:659-661
    public Diagnostic[] GetSemanticDiagnostics(CancellationToken ctx, SourceFile? sourceFile) =>
        CollectCheckerDiagnostics(ctx, sourceFile, GetSemanticDiagnosticsWithChecker);

    // program.go:663-670
    public Dictionary<SourceFile, Diagnostic[]> GetSemanticDiagnosticsWithoutNoEmitFiltering(CancellationToken ctx, SourceFile[] sourceFiles)
    {
        var allDiags = CollectCheckerDiagnosticsFromFiles(ctx, sourceFiles, GetBindAndCheckDiagnosticsWithChecker);
        var result = new Dictionary<SourceFile, Diagnostic[]>(sourceFiles.Length);
        for (var i = 0; i < allDiags.Length; i++)
        {
            result[sourceFiles[i]] = SortAndDeduplicateDiagnostics(allDiags[i]);
        }
        return result;
    }

    // program.go:672-674
    public Diagnostic[] GetSuggestionDiagnostics(CancellationToken ctx, SourceFile? sourceFile) =>
        CollectCheckerDiagnostics(ctx, sourceFile, GetSuggestionDiagnosticsWithChecker);

    // program.go:1415-1425
    private Diagnostic[] GetSuggestionDiagnosticsWithChecker(CancellationToken ctx, global::Ts.Checker.Checker fileChecker, SourceFile sourceFile)
    {
        if (SkipTypeChecking(sourceFile, false))
        {
            return [];
        }
        // Checker creation forces binding, so bind suggestion diagnostics will be populated.
        return CoreUtil.Concatenate(sourceFile.BindSuggestionDiagnostics, fileChecker.GetSuggestionDiagnostics(sourceFile)) ?? [];
    }

    // program.go:676-681 — program (options) + include-processor global diagnostics.
    public Diagnostic[] GetProgramDiagnostics()
    {
        return SortAndDeduplicateDiagnostics(CoreUtil.Concatenate(
            programDiagnostics.ToArray(),
            processed.IncludeProcessor.GetDiagnostics(this).GetGlobalDiagnostics()) ?? []);
    }

    // program.go:683-689 — include-processor per-file diagnostics.
    public Diagnostic[] GetIncludeProcessorDiagnostics(SourceFile sourceFile)
    {
        if (SkipTypeChecking(sourceFile, false))
        {
            return [];
        }
        var (filtered, _) = GetDiagnosticsWithPrecedingDirectives(
            sourceFile,
            processed.IncludeProcessor.GetDiagnostics(this).GetDiagnosticsForFile(sourceFile.FileName()));
        return filtered;
    }

    // program.go:1295-1305
    public Diagnostic[] GetGlobalDiagnostics(CancellationToken ctx)
    {
        if (processed.Files.Length == 0)
        {
            return [];
        }
        if (compilerCheckerPool is not null)
        {
            return compilerCheckerPool.GetGlobalDiagnostics();
        }
        // For external pools (project system), global diagnostics are collected
        // incrementally as checkers are used, not via a bulk query.
        return [];
    }

    // program.go:1307-1309
    public Diagnostic[] GetDeclarationDiagnostics(CancellationToken ctx, SourceFile? sourceFile) =>
        CollectDiagnostics(ctx, sourceFile, true /*concurrent*/, GetDeclarationDiagnosticsForFile);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<SourceFile, Diagnostic[]> declarationDiagnosticCache = new(global::System.Collections.Generic.ReferenceEqualityComparer.Instance);

    // program.go:1399-1414 — getDeclarationDiagnosticsForFile.
    private Diagnostic[] GetDeclarationDiagnosticsForFile(CancellationToken ctx, SourceFile sourceFile)
    {
        if (sourceFile.IsDeclarationFile)
        {
            return [];
        }

        if (declarationDiagnosticCache.TryGetValue(sourceFile, out var cached))
        {
            return cached;
        }

        var (host, done) = EmitHostImpl.NewEmitHost(ctx, this, sourceFile);
        try
        {
            var diagnostics = Emitter_GetDeclarationDiagnostics(host, sourceFile);
            return declarationDiagnosticCache.GetOrAdd(sourceFile, diagnostics);
        }
        finally
        {
            done();
        }
    }

    // emitter.go:521-531 — getDeclarationDiagnostics.
    private Diagnostic[] Emitter_GetDeclarationDiagnostics(EmitHostImpl host, SourceFile file)
    {
        // TODO: use p.getSourceFilesToEmit cache
        var fullFiles = CoreUtil.Filter(GetSourceFilesToEmit(file, false), f => !AstUtilities.IsJsonSourceFile(f)) ?? [];
        if (!CoreUtil.Some(fullFiles, f => f == file))
        {
            return [];
        }
        var options = host.Options();
        var transform = global::Ts.Transformers.Declarations.DeclarationTransformer.NewDeclarationTransformer(host, null, options, "", "");
        transform.TransformSourceFile(file);
        return transform.GetDiagnostics();
    }

    // program.go:691-697
    public bool SkipTypeChecking(SourceFile sourceFile, bool ignoreNoCheck) =>
        (!ignoreNoCheck && Options().NoCheck.IsTrue()) ||
        (Options().SkipLibCheck.IsTrue() && sourceFile.IsDeclarationFile) ||
        (Options().SkipDefaultLibCheck.IsTrue() && IsSourceFileDefaultLibrary(sourceFile.Path())) ||
        Mapper.IsSourceFromProjectReference(sourceFile.Path()) ||
        !CanIncludeBindAndCheckDiagnostics(sourceFile);

    // program.go:699-717
    private bool CanIncludeBindAndCheckDiagnostics(SourceFile sourceFile)
    {
        if (sourceFile.CheckJsDirective is not null && !sourceFile.CheckJsDirective.Enabled)
        {
            return false;
        }

        if (sourceFile.ScriptKind == ScriptKind.TS || sourceFile.ScriptKind == ScriptKind.Tsx || sourceFile.ScriptKind == ScriptKind.External)
        {
            return true;
        }

        bool isJS = sourceFile.ScriptKind == ScriptKind.JS || sourceFile.ScriptKind == ScriptKind.Jsx;
        bool isCheckJS = isJS && IsCheckJSEnabledForFile(sourceFile, Options());
        bool isPlainJS = IsPlainJSFile(sourceFile, Options().CheckJs);

        // By default, only type-check .ts, .tsx, Deferred, plain JS, checked JS and External.
        return isPlainJS || isCheckJS || sourceFile.ScriptKind == ScriptKind.Deferred;
    }

    // program.go:1311-1318
    public static Diagnostic[] FilterNoEmitSemanticDiagnostics(Diagnostic[] diagnostics, CompilerOptions options)
    {
        if (!options.NoEmit.IsTrue())
        {
            return diagnostics;
        }
        return CoreUtil.Filter(diagnostics, d => !d.SkippedOnNoEmit()) ?? [];
    }

    // program.go:1320-1325
    private Diagnostic[] GetSemanticDiagnosticsWithChecker(CancellationToken ctx, global::Ts.Checker.Checker c, SourceFile sourceFile) =>
        CoreUtil.Concatenate(
            FilterNoEmitSemanticDiagnostics(GetBindAndCheckDiagnosticsWithChecker(ctx, c, sourceFile), Options()),
            GetIncludeProcessorDiagnostics(sourceFile)) ?? [];

    // program.go:1330-1362 — bind + checker diagnostics for a single file, with directive filtering.
    private Diagnostic[] GetBindAndCheckDiagnosticsWithChecker(CancellationToken ctx, global::Ts.Checker.Checker fileChecker, SourceFile sourceFile)
    {
        var compilerOptions = Options();
        if (SkipTypeChecking(sourceFile, false))
        {
            return [];
        }

        // Checker creation forces binding, so bind diagnostics will be populated.
        var diags = new List<Diagnostic>(sourceFile.BindDiagnostics() ?? []);
        // PORT-NOTE: C# Checker.GetDiagnostics takes only the SourceFile (no CancellationToken); the Go
        // ctx is threaded through the aggregation shell but the checker query itself is uncancelled here.
        diags.AddRange(fileChecker.GetDiagnostics(sourceFile));

        bool isPlainJS = IsPlainJSFile(sourceFile, compilerOptions.CheckJs);
        if (isPlainJS)
        {
            // program.go:1340-1345 — for plain (unchecked) JS files, report only the allowlisted
            // binder/grammar/type diagnostic codes; everything else the checker produced is dropped.
            return CoreUtil.Filter(diags.ToArray(), d => PlainJSErrors.Contains(d.Code())) ?? [];
        }

        bool isJS = sourceFile.ScriptKind == ScriptKind.JS || sourceFile.ScriptKind == ScriptKind.Jsx;
        bool isCheckJS = isJS && IsCheckJSEnabledForFile(sourceFile, compilerOptions);
        if (isCheckJS)
        {
            var jsdoc = sourceFile.JSDocDiagnostics();
            if (jsdoc is not null)
            {
                diags.AddRange(jsdoc);
            }
        }

        var (filtered, directivesByLine) = GetDiagnosticsWithPrecedingDirectives(sourceFile, diags.ToArray());
        if (directivesByLine is not null)
        {
            // map-iter: 序无关 —— appends an unused-directive diagnostic per remaining @ts-expect-error;
            // the produced set is order-independent (re-sorted by SortAndDeduplicateDiagnostics).
            var result = new List<Diagnostic>(filtered);
            foreach (var directive in directivesByLine.Values)
            {
                // Above we changed all used directive kinds to @ts-ignore, so any @ts-expect-error directives
                // that remain are unused and thus errors.
                if (directive.Kind == CommentDirectiveKind.ExpectError)
                {
                    result.Add(Diagnostic.NewDiagnostic(sourceFile, directive.Loc, global::Ts.Diagnostics.Diagnostics.Unused_ts_expect_error_directive));
                }
            }
            return result.ToArray();
        }
        return filtered;
    }

    // program.go:1364-1397 — filter diagnostics preceded by @ts-ignore/@ts-expect-error directives.
    private (Diagnostic[] Filtered, Dictionary<int, CommentDirective>? DirectivesByLine) GetDiagnosticsWithPrecedingDirectives(SourceFile sourceFile, Diagnostic[] diags)
    {
        if (sourceFile.CommentDirectives is null || sourceFile.CommentDirectives.Length == 0)
        {
            return (diags, null);
        }
        // Build map of directives by line number.
        var directivesByLine = new Dictionary<int, CommentDirective>();
        foreach (var directive in sourceFile.CommentDirectives)
        {
            int line = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, directive.Loc.Pos);
            directivesByLine[line] = directive;
        }
        var lineStarts = global::Ts.Scanner.Scanner.GetECMALineStarts(sourceFile);
        var filtered = new List<Diagnostic>(diags.Length);
        foreach (var diagnostic in diags)
        {
            bool ignoreDiagnostic = false;
            for (int line = global::Ts.Scanner.Scanner.ComputeLineOfPosition(lineStarts, diagnostic.Pos()) - 1; line >= 0; line--)
            {
                // If line contains a @ts-ignore or @ts-expect-error directive, ignore this diagnostic and
                // change the directive kind to @ts-ignore to indicate it was used.
                if (directivesByLine.TryGetValue(line, out var directive))
                {
                    ignoreDiagnostic = true;
                    directivesByLine[line] = directive with { Kind = CommentDirectiveKind.Ignore };
                    break;
                }
                // Stop searching backwards when we encounter a line that isn't blank or a comment.
                if (!IsCommentOrBlankLine(sourceFile.Text(), lineStarts[line]))
                {
                    break;
                }
            }
            if (!ignoreDiagnostic)
            {
                filtered.Add(diagnostic);
            }
        }
        return (filtered.ToArray(), directivesByLine);
    }

    // program.go:1427-1434
    private static bool IsCommentOrBlankLine(string text, int pos)
    {
        while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t'))
        {
            pos++;
        }
        return pos == text.Length ||
            (pos < text.Length && (text[pos] == '\r' || text[pos] == '\n')) ||
            (pos + 1 < text.Length && text[pos] == '/' && text[pos + 1] == '/');
    }

    // program.go:1436-1440
    public static Diagnostic[] SortAndDeduplicateDiagnostics(Diagnostic[] diagnostics)
    {
        diagnostics = (Diagnostic[])diagnostics.Clone(); // slices.Clone
        Array.Sort(diagnostics, DiagnosticsCollection.CompareDiagnostics);
        return CompactAndMergeRelatedInfos(diagnostics);
    }

    // program.go:1444-1473 — remove duplicate diagnostics and, for sequences that differ only by related
    // information, create a single diagnostic with sorted + deduplicated related information.
    private static Diagnostic[] CompactAndMergeRelatedInfos(Diagnostic[] diagnostics)
    {
        if (diagnostics.Length < 2)
        {
            return diagnostics;
        }
        int i = 0;
        int j = 0;
        while (i < diagnostics.Length)
        {
            var d = diagnostics[i];
            int n = 1;
            while (i + n < diagnostics.Length && DiagnosticsCollection.EqualDiagnosticsNoRelatedInfo(d, diagnostics[i + n]))
            {
                n++;
            }
            if (n > 1)
            {
                List<Diagnostic>? relatedInfos = null;
                for (int k = 0; k < n; k++)
                {
                    var ri = diagnostics[i + k].RelatedInformation();
                    if (ri is not null && ri.Length > 0)
                    {
                        (relatedInfos ??= []).AddRange(ri);
                    }
                }
                if (relatedInfos is not null)
                {
                    var arr = relatedInfos.ToArray();
                    Array.Sort(arr, DiagnosticsCollection.CompareDiagnostics);
                    arr = CompactFunc(arr, DiagnosticsCollection.EqualDiagnostics);
                    d = d.Clone().SetRelatedInfo(arr);
                }
            }
            diagnostics[j] = d;
            i += n;
            j++;
        }
        // clear(diagnostics[j:]) + return diagnostics[:j]
        var result = new Diagnostic[j];
        Array.Copy(diagnostics, result, j);
        return result;
    }

    // program.go:1755-1791 — the CLI's top-level diagnostics collector (used by EmitFilesAndReportErrors).
    // GetDiagnosticsOfAnyProgram gathers config-parse, syntactic, program, global and semantic diagnostics
    // for a Program, short-circuiting the way tsgo does when earlier phases already produced errors.
    public static Diagnostic[] GetDiagnosticsOfAnyProgram(
        CancellationToken ctx,
        ProgramLike program,
        SourceFile? file,
        bool skipNoEmitCheckForDtsDiagnostics,
        Func<CancellationToken, SourceFile?, Diagnostic[]> getBindDiagnostics,
        Func<CancellationToken, SourceFile?, Diagnostic[]> getSemanticDiagnostics)
    {
        var allDiagnostics = new List<Diagnostic>(program.GetConfigFileParsingDiagnostics());
        int configFileParsingDiagnosticsLength = allDiagnostics.Count;

        allDiagnostics.AddRange(program.GetSyntacticDiagnostics(ctx, file));

        // If we didn't have any syntactic errors, then also try getting the program (options),
        // global and semantic errors.
        if (allDiagnostics.Count == configFileParsingDiagnosticsLength)
        {
            allDiagnostics.AddRange(program.GetProgramDiagnostics());

            // Do binding early so we can track the time.
            getBindDiagnostics(ctx, file);

            if (program.Options().ListFilesOnly.IsFalseOrUnknown())
            {
                allDiagnostics.AddRange(program.GetGlobalDiagnostics(ctx));

                if (allDiagnostics.Count == configFileParsingDiagnosticsLength)
                {
                    allDiagnostics.AddRange(getSemanticDiagnostics(ctx, file));
                    // Ask for the global diagnostics again (they were empty above); we may have found new
                    // during checking, e.g. missing globals.
                    allDiagnostics.AddRange(program.GetGlobalDiagnostics(ctx));
                }

                // PORT-NOTE: the declaration-diagnostics branch (program.go:1785-1787) needs the emit host
                // (phase 6, GetEmitDeclarations); deferred out of the type-check-and-report subset.
            }
        }
        return allDiagnostics.ToArray();
    }

    // -----------------------------------------------------------------------------------------------
    // Local ports of small pure ast helpers. AstUtilities.IsCheckJSEnabledForFile / IsPlainJSFile are
    // deferred stubs in the (parallel-owned) Ast surface; these faithful copies of ast.go:2863-2871 keep
    // the diagnostics path within this file's boundary without touching Ast/.
    // -----------------------------------------------------------------------------------------------

    // ast.go:2863-2868
    private static bool IsCheckJSEnabledForFile(SourceFile sourceFile, CompilerOptions compilerOptions)
    {
        if (sourceFile.CheckJsDirective is not null)
        {
            return sourceFile.CheckJsDirective.Enabled;
        }
        return compilerOptions.CheckJs == Tristate.True;
    }

    // ast.go:2870-2872
    private static bool IsPlainJSFile(SourceFile? file, Tristate checkJs) =>
        file is not null &&
        (file.ScriptKind == ScriptKind.JS || file.ScriptKind == ScriptKind.Jsx) &&
        file.CheckJsDirective is null &&
        checkJs == Tristate.Unknown;

    // slices.Concat: flatten a jagged array of diagnostic arrays into one array (nulls treated as empty).
    private static Diagnostic[] Concat(Diagnostic[][] arrays)
    {
        int total = 0;
        foreach (var a in arrays)
        {
            total += a?.Length ?? 0;
        }
        var result = new Diagnostic[total];
        int offset = 0;
        foreach (var a in arrays)
        {
            if (a is not null && a.Length > 0)
            {
                Array.Copy(a, 0, result, offset, a.Length);
                offset += a.Length;
            }
        }
        return result;
    }

    // slices.CompactFunc: collapse runs of adjacent elements deemed equal by eq (input already sorted).
    private static Diagnostic[] CompactFunc(Diagnostic[] arr, Func<Diagnostic, Diagnostic, bool> eq)
    {
        if (arr.Length < 2)
        {
            return arr;
        }
        int j = 1;
        for (int i = 1; i < arr.Length; i++)
        {
            if (!eq(arr[i], arr[i - 1]))
            {
                arr[j] = arr[i];
                j++;
            }
        }
        var result = new Diagnostic[j];
        Array.Copy(arr, result, j);
        return result;
    }
}
