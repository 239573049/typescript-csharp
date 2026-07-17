// Ported from tsgo internal/testutil/tsbaseline/js_emit_baseline.go — the `.js` emit baseline writer
// (phase 6 currency). The DoJSEmitBaseline core: the `//// [header] ////` + per-source sections,
// the emitted JS sections (with the JSON parse-error arm), the appended `.d.ts` sections, the
// `//// [DtsFileErrors]` re-compilation section, and the noCheck double-emit comparison.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ts.Ast;
using Ts.Core;

namespace Ts.TestUtil;

public static class JsEmitBaseline
{
    public sealed record EmittedFile(string UnitName, string Content);

    // js_emit_baseline.go:20-146 — DoJSEmitBaseline (content assembly + baseline comparison).
    // `dtsFileErrorsSection` carries the pre-rendered error baseline for the declaration
    // re-compilation (go:77-97, computed by the caller which owns the harness scaffolding);
    // `repeatWithNoCheck` re-runs the compilation with noCheck=true (go:99-132).
    public static void Run(
        IBaselineTestContext t,
        string baselinePath,
        string header,
        IReadOnlyList<BaselineTestFile> tsSources,
        IReadOnlyList<EmittedFile> jsFiles,
        IReadOnlyList<EmittedFile> dtsFiles,
        Baseline.Options opts,
        int diagnosticsCount = 0,
        string? dtsFileErrorsSection = null,
        CompilerOptions? options = null,
        Func<(IReadOnlyList<EmittedFile> Js, IReadOnlyList<EmittedFile> Dts)>? repeatWithNoCheck = null)
    {
        // check js output
        var tsCode = new StringBuilder();
        tsCode.Append("//// [").Append(header).Append("] ////\r\n\r\n");
        for (var i = 0; i < tsSources.Count; i++)
        {
            var file = tsSources[i];
            tsCode.Append("//// [").Append(GetBaseFileName(file.UnitName)).Append("]\r\n");
            tsCode.Append(file.Content);
            if (i < tsSources.Count - 1)
            {
                tsCode.Append("\r\n");
            }
        }

        var jsCode = new StringBuilder();
        foreach (var file in jsFiles)
        {
            if (jsCode.Length > 0 && !EndsWithNewLine(jsCode))
            {
                jsCode.Append("\r\n");
            }
            // js_emit_baseline.go:57-66 — emitted .json outputs that fail to re-parse as JSON are
            // baselined as their parse-error text instead of their content.
            if (diagnosticsCount == 0 && file.UnitName.EndsWith(".json", StringComparison.Ordinal))
            {
                var fileParseResult = global::Ts.Parser.Parser.ParseSourceFile(new SourceFileParseOptions
                {
                    FileName = file.UnitName,
                    Path = new global::Ts.TsPath.Path(file.UnitName),
                }, file.Content, ScriptKind.Json);
                var parseDiagnostics = fileParseResult.Diagnostics() ?? [];
                if (parseDiagnostics.Length > 0)
                {
                    jsCode.Append(ErrorBaseline.GetErrorBaseline(
                        [new BaselineTestFile(file.UnitName, file.Content)],
                        parseDiagnostics));
                    continue;
                }
            }
            jsCode.Append(FileOutput(file));
        }

        if (dtsFiles.Count > 0)
        {
            jsCode.Append("\r\n\r\n");
            foreach (var declFile in dtsFiles)
            {
                jsCode.Append(FileOutput(declFile));
            }
        }

        // js_emit_baseline.go:87-97 — declaration re-compilation errors.
        if (dtsFileErrorsSection != null)
        {
            jsCode.Append("\r\n\r\n//// [DtsFileErrors]\r\n");
            jsCode.Append("\r\n\r\n");
            jsCode.Append(dtsFileErrorsSection);
        }

        // js_emit_baseline.go:99-132 — noCheck double-emit comparison.
        if (options != null && repeatWithNoCheck != null && !options.NoCheck.IsTrue() && !options.NoEmit.IsTrue())
        {
            var withoutChecking = repeatWithNoCheck();

            void CompareResultFileSets(IReadOnlyList<EmittedFile> a, IReadOnlyList<EmittedFile> b)
            {
                var byName = new Dictionary<string, EmittedFile>(StringComparer.Ordinal);
                foreach (var file in b)
                {
                    byName[file.UnitName] = file;
                }
                foreach (var doc in a)
                {
                    byName.TryGetValue(doc.UnitName, out var original);
                    if (original == null)
                    {
                        jsCode.Append("\r\n\r\n!!!! File ");
                        jsCode.Append(ErrorBaseline.RemoveTestPathPrefixes(doc.UnitName, false /*retainTrailingDirectorySeparator*/));
                        jsCode.Append(" missing from original emit, but present in noCheck emit\r\n");
                        jsCode.Append(FileOutput(doc));
                    }
                    else if (original.Content != doc.Content)
                    {
                        jsCode.Append("\r\n\r\n!!!! File ");
                        jsCode.Append(ErrorBaseline.RemoveTestPathPrefixes(doc.UnitName, false /*retainTrailingDirectorySeparator*/));
                        jsCode.Append(" differs from original emit in noCheck emit\r\n");
                        var fileName = GetBaseFileName(doc.UnitName);
                        jsCode.Append("//// [");
                        jsCode.Append(fileName);
                        jsCode.Append("]\r\n");
                        var expected = original.Content;
                        var actual = doc.Content;
                        jsCode.Append(Baseline.DiffText("Expected\tThe full check baseline", "Actual\twith noCheck set", expected, actual));
                    }
                }
            }

            CompareResultFileSets(withoutChecking.Dts, dtsFiles);
            CompareResultFileSets(withoutChecking.Js, jsFiles);
        }

        if (baselinePath.EndsWith(".ts", StringComparison.Ordinal) || baselinePath.EndsWith(".tsx", StringComparison.Ordinal))
        {
            baselinePath = Path.ChangeExtension(baselinePath, ".js");
        }

        string actualText;
        if (jsCode.Length > 0)
        {
            actualText = tsCode.ToString() + "\r\n\r\n" + jsCode.ToString();
        }
        else
        {
            actualText = Baseline.NoContent;
        }

        Baseline.Run(t, baselinePath, actualText, opts);
    }

    // js_emit_baseline.go:148-156 — fileOutput (FullEmitPaths=false form; the conformance corpus
    // never sets fullEmitPaths).
    private static string FileOutput(EmittedFile file) =>
        "//// [" + GetBaseFileName(file.UnitName) + "]\r\n" + file.Content;

    private static string GetBaseFileName(string path)
    {
        var idx = path.TrimEnd('/').LastIndexOf('/');
        return idx < 0 ? path : path[(idx + 1)..];
    }

    private static bool EndsWithNewLine(StringBuilder b) => b.Length > 0 && b[^1] == '\n';
}
