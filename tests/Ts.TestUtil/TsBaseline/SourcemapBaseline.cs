// Ported from tsgo internal/testutil/tsbaseline/sourcemap_baseline.go (DoSourcemapBaseline —
// the `.js.map` baseline writer) and sourcemap_record_baseline.go (DoSourcemapRecordBaseline —
// the `.sourcemap.txt` baseline writer).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ts.Core;

namespace Ts.TestUtil;

public static class SourcemapBaseline
{
    // sourcemap_baseline.go:18-68 — DoSourcemapBaseline. `maps` are the emitted .map outputs in
    // emit order; `jsFileCount`/`jsFileCountIncludingJson` mirror result.GetNumberOfJSFiles.
    public static void DoSourcemapBaseline(
        IBaselineTestContext t,
        string baselinePath,
        CompilerOptions options,
        IReadOnlyList<JsEmitBaseline.EmittedFile> maps,
        int jsFileCount,
        int jsFileCountIncludingJson,
        IReadOnlyList<JsEmitBaseline.EmittedFile> outputs,
        IReadOnlyList<JsEmitBaseline.EmittedFile> inputs,
        int diagnosticsCount,
        Baseline.Options opts)
    {
        var declMaps = options.GetAreDeclarationMapsEnabled();
        if (options.InlineSourceMap.IsTrue())
        {
            if (maps.Count > 0 && !declMaps)
            {
                t.Fatal("No sourcemap files should be generated if inlineSourceMaps was set.");
            }
            return;
        }
        else if (options.SourceMap.IsTrue() || declMaps)
        {
            var expectedMapCount = 0;
            if (options.SourceMap.IsTrue())
            {
                expectedMapCount += jsFileCount;
            }
            if (declMaps)
            {
                expectedMapCount += jsFileCountIncludingJson;
            }
            if (maps.Count != expectedMapCount)
            {
                t.Fatal("Number of sourcemap files should be same as js files.");
            }

            string sourceMapCode;
            if ((options.NoEmitOnError.IsTrue() && diagnosticsCount != 0) || maps.Count == 0)
            {
                sourceMapCode = Baseline.NoContent;
            }
            else
            {
                var sourceMapCodeBuilder = new StringBuilder();
                foreach (var sourceMap in maps)
                {
                    if (sourceMapCodeBuilder.Length > 0)
                    {
                        sourceMapCodeBuilder.Append("\r\n");
                    }
                    sourceMapCodeBuilder.Append(FileOutput(sourceMap));
                    if (!options.InlineSourceMap.IsTrue())
                    {
                        sourceMapCodeBuilder.Append(CreateSourceMapPreviewLink(sourceMap, outputs, inputs));
                    }
                }
                sourceMapCode = sourceMapCodeBuilder.ToString();
            }

            if (baselinePath.EndsWith(".ts", StringComparison.Ordinal) || baselinePath.EndsWith(".tsx", StringComparison.Ordinal))
            {
                baselinePath = Path.ChangeExtension(baselinePath, ".js.map");
            }

            Baseline.Run(t, baselinePath, sourceMapCode, opts);
        }
    }

    // sourcemap_record_baseline.go:12-34 — DoSourcemapRecordBaseline.
    public static void DoSourcemapRecordBaseline(
        IBaselineTestContext t,
        string baselinePath,
        CompilerOptions options,
        string sourceMapRecord,
        int diagnosticsCount,
        Baseline.Options opts)
    {
        var actual = Baseline.NoContent;
        if (options.SourceMap.IsTrue() || options.InlineSourceMap.IsTrue() || options.DeclarationMap.IsTrue())
        {
            var record = ErrorBaseline.RemoveTestPathPrefixes(sourceMapRecord, false /*retainTrailingDirectorySeparator*/);
            if (!(options.NoEmitOnError.IsTrue() && diagnosticsCount > 0) && record.Length > 0)
            {
                actual = record;
            }
        }

        if (baselinePath.EndsWith(".ts", StringComparison.Ordinal) || baselinePath.EndsWith(".tsx", StringComparison.Ordinal))
        {
            baselinePath = Path.ChangeExtension(baselinePath, ".sourcemap.txt");
        }

        Baseline.Run(t, baselinePath, actual, opts);
    }

    // js_emit_baseline.go:152-160 fileOutput (FullEmitPaths=false form, as in JsEmitBaseline).
    private static string FileOutput(JsEmitBaseline.EmittedFile file) =>
        "//// [" + GetBaseFileName(file.UnitName) + "]\r\n" + file.Content;

    private static string GetBaseFileName(string path)
    {
        var idx = path.TrimEnd('/').LastIndexOf('/');
        return idx < 0 ? path : path[(idx + 1)..];
    }

    // sourcemap_baseline.go:70-115 — createSourceMapPreviewLink.
    private static string CreateSourceMapPreviewLink(
        JsEmitBaseline.EmittedFile sourceMap,
        IReadOnlyList<JsEmitBaseline.EmittedFile> outputs,
        IReadOnlyList<JsEmitBaseline.EmittedFile> inputs)
    {
        var sourcemapJSON = global::Ts.SourceMap.SourceMapper.TryParseRawSourceMap(sourceMap.Content);
        if (sourcemapJSON == null)
        {
            throw new InvalidOperationException("failed to parse raw source map");
        }

        JsEmitBaseline.EmittedFile? outputJSFile = null;
        foreach (var td in outputs)
        {
            if (td.UnitName.EndsWith(sourcemapJSON.File, StringComparison.Ordinal))
            {
                outputJSFile = td;
                break;
            }
        }

        // !!! Strada uses a fallible approach to associating inputs and outputs derived from a
        // !!! source map output (see the commented logic in sourcemap_baseline.go:80-102).

        if (outputJSFile == null)
        {
            return "";
        }

        var sourceTDs = new List<JsEmitBaseline.EmittedFile?>();
        foreach (var s in sourcemapJSON.Sources)
        {
            JsEmitBaseline.EmittedFile? found = null;
            foreach (var td in inputs)
            {
                if (td.UnitName.EndsWith(s, StringComparison.Ordinal))
                {
                    found = td;
                    break;
                }
            }
            sourceTDs.Add(found);
        }
        if (sourceTDs.Contains(null))
        {
            return "";
        }

        var hash = new StringBuilder();
        hash.Append("\n//// https://sokra.github.io/source-map-visualization#base64,");
        hash.Append(Base64EncodeChunk(outputJSFile.Content));
        hash.Append(',');
        hash.Append(Base64EncodeChunk(sourceMap.Content));
        foreach (var td in sourceTDs)
        {
            hash.Append(',');
            hash.Append(Base64EncodeChunk(td!.Content));
        }
        hash.Append('\n');
        return hash.ToString();
    }

    // sourcemap_baseline.go:117-124 — the Go QueryEscape/QueryUnescape round-trip is an identity
    // transform; the observable behavior is a base64 encoding of the UTF-8 bytes.
    private static string Base64EncodeChunk(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}
