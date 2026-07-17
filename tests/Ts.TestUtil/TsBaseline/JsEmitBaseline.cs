// Ported from tsgo internal/testutil/tsbaseline/js_emit_baseline.go — the `.js` emit baseline writer
// (phase 6 currency). The DoJSEmitBaseline core: the `//// [header] ////` + per-source sections,
// the emitted JS sections, and the appended `.d.ts` sections.
//
// PORT-NOTE (deferred sections, each only contributes bytes in narrower situations):
//   * the JSON parse-error arm (js_emit_baseline.go:56-66) — only for emitted .json outputs;
//   * the declaration re-compilation + `//// [DtsFileErrors]` section (go:75-99) — only when the
//     emitted .d.ts files themselves fail to compile;
//   * the noCheck double-emit comparison (go:101-135) — only appends when noCheck emit DIFFERS.
// These land with the phase-6 hardening wave; cases needing them stay on the failingJs allowlist.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ts.TestUtil;

public static class JsEmitBaseline
{
    public sealed record EmittedFile(string UnitName, string Content);

    // js_emit_baseline.go:20-150 — DoJSEmitBaseline (content assembly + baseline comparison).
    public static void Run(
        IBaselineTestContext t,
        string baselinePath,
        string header,
        IReadOnlyList<BaselineTestFile> tsSources,
        IReadOnlyList<EmittedFile> jsFiles,
        IReadOnlyList<EmittedFile> dtsFiles,
        Baseline.Options opts)
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

        if (baselinePath.EndsWith(".ts", StringComparison.Ordinal) || baselinePath.EndsWith(".tsx", StringComparison.Ordinal))
        {
            baselinePath = Path.ChangeExtension(baselinePath, ".js");
        }

        string actual;
        if (jsCode.Length > 0)
        {
            actual = tsCode.ToString() + "\r\n\r\n" + jsCode.ToString();
        }
        else
        {
            actual = Baseline.NoContent;
        }

        Baseline.Run(t, baselinePath, actual, opts);
    }

    // js_emit_baseline.go:152-160 — fileOutput (FullEmitPaths=false form; the conformance corpus
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
