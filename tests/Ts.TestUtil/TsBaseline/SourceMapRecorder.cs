// Ported from tsgo internal/testutil/harnessutil/sourcemap_recorder.go — the `.sourcemap.txt`
// record writer — plus CompilationResult.GetSourceMapRecord (harnessutil.go:885-920).
//
// Byte-semantics shim (plan/01 §"两个刻意的兼容垫片"): the continuation-marker end column at
// sourcemap_recorder.go:350-353 counts Go string BYTES (`len(jsFileText)-1`); C# uses
// Encoding.UTF8.GetByteCount to reproduce the byte count on non-ASCII lines.

using System;
using System.Collections.Generic;
using System.Text;
using Ts.SourceMap;

namespace Ts.TestUtil;

public static class SourceMapRecorderUtil
{
    // harnessutil.go:885-920 — CompilationResult.GetSourceMapRecord, decoupled from
    // CompilationResult: callers pass the emitted js/dts contents and a source-file text lookup.
    public static string GetSourceMapRecord(
        IReadOnlyList<global::Ts.Compiler.SourceMapEmitResult> sourceMaps,
        Func<string, JsEmitBaseline.EmittedFile?> getGeneratedFile,
        Func<string, string?> getSourceFileText)
    {
        if (sourceMaps.Count == 0)
        {
            return "";
        }

        var sourceMapRecorder = new WriterAggregator();
        foreach (var sourceMapData in sourceMaps)
        {
            string? prevSourceFile = null;
            var currentFile = getGeneratedFile(sourceMapData.GeneratedFile);

            var sourceMapSpanWriter = new SourceMapSpanWriter(sourceMapRecorder, sourceMapData.SourceMap!, currentFile!);
            var mapper = MappingsDecoder.DecodeMappings(sourceMapData.SourceMap!.Mappings);
            while (true)
            {
                var (decodedSourceMapping, done) = mapper.Next();
                if (done)
                {
                    break;
                }
                string? currentSourceFileName = null;
                string? currentSourceFileText = null;
                if (decodedSourceMapping!.IsSourceMapping())
                {
                    currentSourceFileName = sourceMapData.InputSourceFileNames![decodedSourceMapping.SourceIndex];
                    currentSourceFileText = getSourceFileText(currentSourceFileName);
                }
                if (currentSourceFileName != prevSourceFile)
                {
                    if (currentSourceFileText != null)
                    {
                        sourceMapSpanWriter.RecordNewSourceFileSpan(decodedSourceMapping, currentSourceFileText);
                    }
                    prevSourceFile = currentSourceFileName;
                }
                else
                {
                    sourceMapSpanWriter.RecordSourceMapSpan(decodedSourceMapping);
                }
            }
            sourceMapSpanWriter.Close();
        }
        return sourceMapRecorder.ToString();
    }
}

// sourcemap_recorder.go:16-31
internal sealed class WriterAggregator
{
    private readonly StringBuilder builder = new();

    internal void WriteString(string s) => builder.Append(s);

    internal void WriteLine(string s)
    {
        builder.Append(s);
        builder.Append("\r\n");
    }

    public override string ToString() => builder.ToString();
}

// sourcemap_recorder.go:33-36
internal sealed class SourceMapSpanWithDecodeErrors
{
    internal Mapping sourceMapSpan = null!;
    internal List<string> decodeErrors = new();
}

// sourcemap_recorder.go:38-41
internal sealed class DecodedMapping
{
    internal Mapping sourceMapSpan = null!;
    internal string? error;
}

// sourcemap_recorder.go:43-76
internal sealed class HarnessSourceMapDecoder
{
    private readonly string sourceMapMappings;
    private readonly MappingsDecoder mappings;

    internal HarnessSourceMapDecoder(RawSourceMap sourceMap)
    {
        sourceMapMappings = sourceMap.Mappings;
        mappings = MappingsDecoder.DecodeMappings(sourceMap.Mappings);
    }

    internal DecodedMapping DecodeNextEncodedSourceMapSpan()
    {
        var (value, done) = mappings.Next();
        if (done)
        {
            var mapping = new DecodedMapping
            {
                error = mappings.Error(),
                sourceMapSpan = mappings.State(),
            };
            mapping.error ??= "No encoded entry found";
            return mapping;
        }
        return new DecodedMapping { sourceMapSpan = value! };
    }

    internal bool HasCompletedDecoding() => mappings.Pos() == sourceMapMappings.Length;

    internal string GetRemainingDecodeString() => sourceMapMappings[mappings.Pos()..];
}

// sourcemap_recorder.go:78-235
internal sealed class SourceMapSpanWriter
{
    private readonly WriterAggregator sourceMapRecorder;
    private readonly string[] sourceMapSources;
    private readonly string[] sourceMapNames;
    private readonly JsEmitBaseline.EmittedFile jsFile;
    private readonly int[] jsLineMap;
    internal string tsCode = "";
    internal int[] tsLineMap = [];
    private List<SourceMapSpanWithDecodeErrors> spansOnSingleLine = new();
    internal int prevWrittenSourcePos;
    private int nextJsLineToWrite;
    internal bool spanMarkerContinues;
    private readonly HarnessSourceMapDecoder sourceMapDecoder;

    // sourcemap_recorder.go:93-121
    internal SourceMapSpanWriter(WriterAggregator sourceMapRecorder, RawSourceMap sourceMap, JsEmitBaseline.EmittedFile jsFile)
    {
        this.sourceMapRecorder = sourceMapRecorder;
        sourceMapSources = sourceMap.Sources;
        sourceMapNames = sourceMap.Names;
        this.jsFile = jsFile;
        jsLineMap = global::Ts.Core.CoreUtil.ComputeECMALineStarts(jsFile.Content);
        sourceMapDecoder = new HarnessSourceMapDecoder(sourceMap);

        sourceMapRecorder.WriteLine("===================================================================");
        sourceMapRecorder.WriteLine($"JsFile: {sourceMap.File}");
        var mapUrl = global::Ts.SourceMap.Util.TryGetSourceMappingURL(ECMALineInfo.CreateECMALineInfo(jsFile.Content, jsLineMap));
        sourceMapRecorder.WriteLine($"mapUrl: {mapUrl}");
        sourceMapRecorder.WriteLine($"sourceRoot: {sourceMap.SourceRoot}");
        sourceMapRecorder.WriteLine($"sources: {string.Join(",", sourceMap.Sources)}");
        if (sourceMap.SourcesContent != null && sourceMap.SourcesContent.Length > 0)
        {
            sourceMapRecorder.WriteLine($"sourcesContent: {MarshalSourcesContent(sourceMap.SourcesContent)}");
        }
        sourceMapRecorder.WriteLine("===================================================================");
    }

    // Go json.Marshal([]*string) — byte-faithful compact JSON for the sourcesContent array
    // (Utf8JsonWriter + relaxed escaping per plan/07 §4.2-6, same policy as the RawSourceMap writer).
    private static string MarshalSourcesContent(string?[] sourcesContent)
    {
        var buffer = new global::System.IO.MemoryStream();
        var options = new global::System.Text.Json.JsonWriterOptions
        {
            Indented = false,
            Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using (var writer = new global::System.Text.Json.Utf8JsonWriter(buffer, options))
        {
            writer.WriteStartArray();
            foreach (var s in sourcesContent)
            {
                if (s == null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStringValue(s);
                }
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // sourcemap_recorder.go:123-137
    private string GetSourceMapSpanString(Mapping mapEntry, bool getAbsentNameIndex)
    {
        var mapString = new StringBuilder();
        mapString.Append($"Emitted({mapEntry.GeneratedLine + 1}, {mapEntry.GeneratedCharacter + 1})");
        if (mapEntry.IsSourceMapping())
        {
            mapString.Append($" Source({mapEntry.SourceLine + 1}, {mapEntry.SourceCharacter + 1}) + SourceIndex({mapEntry.SourceIndex})");
            if (mapEntry.NameIndex >= 0 && mapEntry.NameIndex < sourceMapNames.Length)
            {
                mapString.Append($" name ({sourceMapNames[mapEntry.NameIndex]})");
            }
            else
            {
                if (mapEntry.NameIndex != Mapping.MissingName || getAbsentNameIndex)
                {
                    mapString.Append($" nameIndex ({mapEntry.NameIndex})");
                }
            }
        }
        return mapString.ToString();
    }

    // sourcemap_recorder.go:139-167
    internal void RecordSourceMapSpan(Mapping sourceMapSpan)
    {
        // verify the decoded span is same as the new span
        var decodeResult = sourceMapDecoder.DecodeNextEncodedSourceMapSpan();
        var decodeErrors = new List<string>();
        if (decodeResult.error != null || !decodeResult.sourceMapSpan.Equals(sourceMapSpan))
        {
            if (decodeResult.error != null)
            {
                decodeErrors.Add("!!^^ !!^^ There was decoding error in the sourcemap at this location: " + decodeResult.error);
            }
            else
            {
                decodeErrors.Add("!!^^ !!^^ The decoded span from sourcemap's mapping entry does not match what was encoded for this span:");
            }
            decodeErrors.Add(
                "!!^^ !!^^ Decoded span from sourcemap's mappings entry: " +
                GetSourceMapSpanString(decodeResult.sourceMapSpan, true /*getAbsentNameIndex*/) +
                " Span encoded by the emitter:" +
                GetSourceMapSpanString(sourceMapSpan, true /*getAbsentNameIndex*/));
        }

        if (spansOnSingleLine.Count > 0 && spansOnSingleLine[0].sourceMapSpan.GeneratedLine != sourceMapSpan.GeneratedLine)
        {
            // On different line from the one that we have been recording till now,
            WriteRecordedSpans();
            spansOnSingleLine = new List<SourceMapSpanWithDecodeErrors>();
        }
        spansOnSingleLine.Add(new SourceMapSpanWithDecodeErrors
        {
            sourceMapSpan = sourceMapSpan,
            decodeErrors = decodeErrors,
        });
    }

    // sourcemap_recorder.go:169-196
    internal void RecordNewSourceFileSpan(Mapping sourceMapSpan, string newSourceFileCode)
    {
        var continuesLine = false;
        if (spansOnSingleLine.Count > 0 && spansOnSingleLine[0].sourceMapSpan.GeneratedCharacter == sourceMapSpan.GeneratedLine)
        { // !!! char == line seems like a bug in Strada?
            WriteRecordedSpans();
            spansOnSingleLine = new List<SourceMapSpanWithDecodeErrors>();
            nextJsLineToWrite--; // walk back one line to reprint the line
            continuesLine = true;
        }

        RecordSourceMapSpan(sourceMapSpan);

        if (spansOnSingleLine.Count != 1)
        {
            throw new InvalidOperationException("expected a single span");
        }

        sourceMapRecorder.WriteLine("-------------------------------------------------------------------");
        if (continuesLine)
        {
            sourceMapRecorder.WriteLine($"emittedFile:{jsFile.UnitName} ({sourceMapSpan.GeneratedLine + 1}, {sourceMapSpan.GeneratedCharacter + 1})");
        }
        else
        {
            sourceMapRecorder.WriteLine($"emittedFile:{jsFile.UnitName}");
        }
        sourceMapRecorder.WriteLine($"sourceFile:{sourceMapSources[spansOnSingleLine[0].sourceMapSpan.SourceIndex]}");
        sourceMapRecorder.WriteLine("-------------------------------------------------------------------");

        tsLineMap = global::Ts.Core.CoreUtil.ComputeECMALineStarts(newSourceFileCode);
        tsCode = newSourceFileCode;
        prevWrittenSourcePos = 0;
    }

    // sourcemap_recorder.go:198-209
    internal void Close()
    {
        // Write the lines pending on the single line
        WriteRecordedSpans();

        if (!sourceMapDecoder.HasCompletedDecoding())
        {
            sourceMapRecorder.WriteLine("!!!! **** There are more source map entries in the sourceMap's mapping than what was encoded");
            sourceMapRecorder.WriteLine($"!!!! **** Remaining decoded string: {sourceMapDecoder.GetRemainingDecodeString()}");
        }

        // write remaining js lines
        WriteJsFileLines(jsLineMap.Length);
    }

    // sourcemap_recorder.go:211-225
    internal string GetTextOfLine(int line, int[] lineMap, string code)
    {
        var startPos = lineMap[line];
        int endPos;
        if (line + 1 < lineMap.Length)
        {
            endPos = lineMap[line + 1];
        }
        else
        {
            endPos = code.Length;
        }
        var text = code[startPos..endPos];
        if (line == 0)
        {
            return global::Ts.StringUtil.StringUtil.RemoveByteOrderMark(text);
        }
        return text;
    }

    // sourcemap_recorder.go:227-231
    internal void WriteJsFileLines(int endJsLine)
    {
        for (; nextJsLineToWrite < endJsLine; nextJsLineToWrite++)
        {
            sourceMapRecorder.WriteString($">>>{GetTextOfLine(nextJsLineToWrite, jsLineMap, jsFile.Content)}");
        }
    }

    // sourcemap_recorder.go:233-236
    private void WriteRecordedSpans()
    {
        new RecordedSpanWriter(this).WriteRecordedSpans();
    }

    // sourcemap_recorder.go:238-364
    private sealed class RecordedSpanWriter
    {
        private readonly List<string> markerIds = new();
        private int prevEmittedCol;
        private readonly SourceMapSpanWriter w;

        internal RecordedSpanWriter(SourceMapSpanWriter w) => this.w = w;

        // sourcemap_recorder.go:244-259
        private string GetMarkerId(int markerIndex)
        {
            string markerId;
            if (w.spanMarkerContinues)
            {
                if (markerIndex != 0)
                {
                    throw new InvalidOperationException("expected markerIndex to be 0");
                }
                markerId = "1->";
            }
            else
            {
                markerId = (markerIndex + 1).ToString(global::System.Globalization.CultureInfo.InvariantCulture);
                if (markerId.Length < 2)
                {
                    markerId += " ";
                }
                markerId += ">";
            }
            return markerId;
        }

        // sourcemap_recorder.go:261-267
        private void IterateSpans(Action<SourceMapSpanWithDecodeErrors, int> fn)
        {
            prevEmittedCol = 0;
            for (var i = 0; i < w.spansOnSingleLine.Count; i++)
            {
                fn(w.spansOnSingleLine[i], i);
                prevEmittedCol = w.spansOnSingleLine[i].sourceMapSpan.GeneratedCharacter;
            }
        }

        // sourcemap_recorder.go:269-274
        private void WriteSourceMapIndent(int indentLength, string indentPrefix)
        {
            w.sourceMapRecorder.WriteString(indentPrefix);
            for (var i = 0; i < indentLength; i++)
            {
                w.sourceMapRecorder.WriteString(" ");
            }
        }

        // sourcemap_recorder.go:276-278
        private void WriteSourceMapMarker(SourceMapSpanWithDecodeErrors currentSpan, int index)
        {
            WriteSourceMapMarkerEx(currentSpan, index, currentSpan.sourceMapSpan.GeneratedCharacter, false /*endContinues*/);
        }

        // sourcemap_recorder.go:280-292
        private void WriteSourceMapMarkerEx(SourceMapSpanWithDecodeErrors? currentSpan, int index, int endColumn, bool endContinues)
        {
            var markerId = GetMarkerId(index);
            markerIds.Add(markerId);
            WriteSourceMapIndent(prevEmittedCol, markerId);
            for (var i = prevEmittedCol; i < endColumn; i++)
            {
                w.sourceMapRecorder.WriteString("^");
            }
            if (endContinues)
            {
                w.sourceMapRecorder.WriteString("->");
            }
            w.sourceMapRecorder.WriteLine("");
            w.spanMarkerContinues = endContinues;
        }

        // sourcemap_recorder.go:294-329
        private void WriteSourceMapSourceText(SourceMapSpanWithDecodeErrors currentSpan, int index)
        {
            // Convert UTF-16 character offset from the source map to a byte position.
            var sourcePos = global::Ts.Scanner.Scanner.ComputePositionOfLineAndUTF16Character(
                w.tsLineMap,
                currentSpan.sourceMapSpan.SourceLine,
                currentSpan.sourceMapSpan.SourceCharacter,
                w.tsCode,
                true /*allowEdits*/);
            var sourceText = "";
            if (w.prevWrittenSourcePos < sourcePos)
            {
                // Position that goes forward, get text
                sourceText = w.tsCode[w.prevWrittenSourcePos..sourcePos];
            }

            // If there are decode errors, write
            foreach (var decodeError in currentSpan.decodeErrors)
            {
                WriteSourceMapIndent(prevEmittedCol, markerIds[index]);
                w.sourceMapRecorder.WriteLine(decodeError);
            }

            var tsCodeLineMap = global::Ts.Core.CoreUtil.ComputeECMALineStarts(sourceText);
            for (var i = 0; i < tsCodeLineMap.Length; i++)
            {
                if (i == 0)
                {
                    WriteSourceMapIndent(prevEmittedCol, markerIds[index]);
                }
                else
                {
                    WriteSourceMapIndent(prevEmittedCol, "  >");
                }
                w.sourceMapRecorder.WriteString(w.GetTextOfLine(i, tsCodeLineMap, sourceText));
                if (i == tsCodeLineMap.Length - 1)
                {
                    w.sourceMapRecorder.WriteLine("");
                }
            }

            w.prevWrittenSourcePos = sourcePos;
        }

        // sourcemap_recorder.go:331-333
        private void WriteSpanDetails(SourceMapSpanWithDecodeErrors currentSpan, int index)
        {
            w.sourceMapRecorder.WriteLine($"{markerIds[index]}{w.GetSourceMapSpanString(currentSpan.sourceMapSpan, false /*getAbsentNameIndex*/)}");
        }

        // sourcemap_recorder.go:335-364
        internal void WriteRecordedSpans()
        {
            if (w.spansOnSingleLine.Count > 0)
            {
                var currentJsLine = w.spansOnSingleLine[0].sourceMapSpan.GeneratedLine;

                // Write js line
                w.WriteJsFileLines(currentJsLine + 1);

                // Emit markers
                IterateSpans(WriteSourceMapMarker);

                var jsFileText = w.GetTextOfLine(currentJsLine + 1, w.jsLineMap, w.jsFile.Content); // TODO: Strada is wrong here, we should be looking at `currentJsLine`, not `currentJsLine+1`
                // Go len(jsFileText) counts BYTES: reproduce with the UTF-8 byte count (plan/01 shim).
                var jsFileTextByteLen = Encoding.UTF8.GetByteCount(jsFileText);
                if (prevEmittedCol < jsFileTextByteLen - 1)
                {
                    // There is remaining text on this line that will be part of next source span so write marker that continues
                    WriteSourceMapMarkerEx(null /*currentSpan*/, w.spansOnSingleLine.Count, jsFileTextByteLen - 1 /*endColumn*/, true /*endContinues*/);
                }

                // Emit Source text
                IterateSpans(WriteSourceMapSourceText);

                // Emit column number etc
                IterateSpans(WriteSpanDetails);

                w.sourceMapRecorder.WriteLine("---");
            }
        }
    }
}
