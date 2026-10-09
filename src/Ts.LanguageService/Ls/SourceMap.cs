// Ported from tsgo internal/ls/source_map.go (1:1).
//
// getMappedLocation / script / getScript / tryGetSourcePosition(+Worker) /
// tryGetGeneratedPosition(+Worker). The generated-position worker needs
// outputpaths.GetOutputDeclarationFileNameWorker (public static on Ts.OutputPaths
// OutputPaths, with the Program as its OutputPathsHost) and
// program.IsSourceFromProjectReference (via ProgramLsExports.IsSourceFromProjectReferenceForLs).
//
// PORT-NOTE (host surface): Go's LanguageService carries l.host.ReadFile /
// l.host.UseCaseSensitiveFileNames / l.GetDocumentPositionMapper from the lsp session
// host. The foundation Ts.Ls Host interface (Ls/Host.cs — shared file, out of scope)
// lacks those members, so this file routes them through:
//   1. an optional ILsSourceMapHost the concrete Host may implement (overlay wave), or
//   2. the Program's own file system (ProgramHost fallback),
// and builds the Ts.SourceMap.Host adapter on the same sources. Go's lsp host
// also supplies per-file ECMALineInfo (line maps from the document overlays); the
// adapter returns null, which DocumentPositionMapper tolerates (all call sites null-check).

using System;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Compiler; // ProgramLsExports extension methods
using Ts.Ls.LsConv; // LineMap.ComputeIndexOfLineStart extension
using Ts.SourceMap; // SourceMapper DocumentPositionMapper extension methods

namespace Ts.Ls;

// source_map.go:39-50 — script.
internal sealed class LsScript : global::Ts.Ls.LsConv.Script
{
    public LsScript(string fileName, string text)
    {
        this.fileName = fileName;
        this.text = text;
        lineMap = global::Ts.Ls.LsConv.LineMap.ComputeLSPLineStarts(text);
    }

    private readonly string fileName;
    private readonly string text;
    private readonly global::Ts.Ls.LsConv.LSPLineMap lineMap;

    public string FileName() => fileName;
    public string Text() => text;

    // converters.go:194-225 over this script's own line map (the lsp session's
    // converters.getter is program-file-bound; script text may be generated output).
    public global::Ts.LsProto.Position PositionToLineAndCharacter(int position)
    {
        position = Math.Max(0, Math.Min(position, text.Length));
        var line = lineMap.ComputeIndexOfLineStart(position);
        line = Math.Max(0, Math.Min(line, lineMap.LineStarts.Count - 1));
        return new global::Ts.LsProto.Position
        {
            Line = (uint)line,
            Character = (uint)(position - lineMap.LineStarts[line]),
        };
    }

    // converters.go:40-45 over this script's own line map.
    public global::Ts.LsProto.Range ToLSPRange(global::Ts.Core.TextRange textRange) =>
        new() { Start = PositionToLineAndCharacter(textRange.Pos), End = PositionToLineAndCharacter(textRange.End) };
}

// Optional extension of Ts.Ls.Host for the document-overlay wave: Go's ls host.ReadFile
// and ls host.UseCaseSensitiveFileNames (host.go). A Host that can read through editor
// overlays implements this; the direct-program fallback reads from the program FS.
public interface ILsSourceMapHost
{
    bool ReadFile(string fileName, out string contents);

    bool UseCaseSensitiveFileNames();
}

public sealed partial class LanguageService
{
    // source_map.go:52-58 — getScript.
    internal LsScript? GetScript(string fileName)
    {
        if (!ReadFileLs(fileName, out var text))
        {
            return null;
        }
        return new LsScript(fileName, text);
    }

    // source_map.go:12-37 — getMappedLocation.
    internal global::Ts.LsProto.Location GetMappedLocation(string fileName, global::Ts.Core.TextRange fileRange)
    {
        var startPos = TryGetSourcePosition(fileName, fileRange.Pos);
        if (startPos == null)
        {
            var lspRange = GetScript(fileName)!.ToLSPRange(fileRange);
            return new global::Ts.LsProto.Location
            {
                Uri = LsDeps.FileNameToDocumentURI(fileName),
                Range = lspRange,
            };
        }
        var endPos = TryGetSourcePosition(fileName, fileRange.End);
        if (endPos == null || endPos.FileName != startPos.FileName || endPos.Pos < startPos.Pos)
        {
            // When end doesn't map, maps to a different source file (e.g. in a .d.ts with a
            // multi-source source map from --outFile compilation), or maps to a position before
            // start (non-monotonic source map mappings), approximate the end position.
            endPos = new global::Ts.SourceMap.DocumentPosition
            {
                FileName = startPos.FileName,
                Pos = startPos.Pos + fileRange.Len,
            };
        }
        var newRange = new global::Ts.Core.TextRange(startPos.Pos, endPos.Pos);
        var lspRangeMapped = GetScript(startPos.FileName)!.ToLSPRange(newRange);
        return new global::Ts.LsProto.Location
        {
            Uri = LsDeps.FileNameToDocumentURI(startPos.FileName),
            Range = lspRangeMapped,
        };
    }

    // source_map.go:60-71 — tryGetSourcePosition.
    internal global::Ts.SourceMap.DocumentPosition? TryGetSourcePosition(string fileName, int position)
    {
        var newPos = TryGetSourcePositionWorker(fileName, position);
        if (newPos != null)
        {
            if (!ReadFileLs(newPos.FileName, out _))
            { // File doesn't exist
                return null;
            }
        }
        return newPos;
    }

    // source_map.go:73-90 — tryGetSourcePositionWorker.
    private global::Ts.SourceMap.DocumentPosition? TryGetSourcePositionWorker(string fileName, int position)
    {
        if (!global::Ts.TsPath.TsPath.IsDeclarationFileName(fileName))
        {
            return null;
        }

        var positionMapper = GetDocumentPositionMapper(fileName);
        var documentPos = positionMapper.GetSourcePosition(
            new global::Ts.SourceMap.DocumentPosition { FileName = fileName, Pos = position });
        if (documentPos == null)
        {
            return null;
        }
        var newPos = TryGetSourcePositionWorker(documentPos.FileName, documentPos.Pos);
        return newPos ?? documentPos;
    }

    // source_map.go:92-103 — tryGetGeneratedPosition.
    internal global::Ts.SourceMap.DocumentPosition? TryGetGeneratedPosition(string fileName, int position)
    {
        var newPos = TryGetGeneratedPositionWorker(fileName, position);
        if (newPos != null)
        {
            if (!ReadFileLs(newPos.FileName, out _))
            { // File doesn't exist
                return null;
            }
        }
        return newPos;
    }

    // source_map.go:105-134 — tryGetGeneratedPositionWorker.
    private global::Ts.SourceMap.DocumentPosition? TryGetGeneratedPositionWorker(string fileName, int position)
    {
        if (global::Ts.TsPath.TsPath.IsDeclarationFileName(fileName))
        {
            return null;
        }

        if (program.GetSourceFile(fileName) == null)
        {
            return null;
        }

        var path = ToPath(fileName);
        // If this is source file of project reference source (instead of redirect) there is no generated position
        if (program.IsSourceFromProjectReferenceForLs(path))
        {
            return null;
        }

        var declarationFileName = global::Ts.OutputPaths.OutputPaths.GetOutputDeclarationFileNameWorker(
            fileName, program.Options(), program);
        var positionMapper = GetDocumentPositionMapper(declarationFileName);
        var documentPos = positionMapper.GetGeneratedPosition(
            new global::Ts.SourceMap.DocumentPosition { FileName = fileName, Pos = position });
        if (documentPos == null)
        {
            return null;
        }
        var newPos = TryGetGeneratedPositionWorker(documentPos.FileName, documentPos.Pos);
        return newPos ?? documentPos;
    }

    // host.go — ls host.UseCaseSensitiveFileNames (program-FS fallback per the file header).
    internal bool UseCaseSensitiveFileNames()
    {
        if (host is ILsSourceMapHost sourceMapHost)
        {
            return sourceMapHost.UseCaseSensitiveFileNames();
        }
        return program.Host().FS().UseCaseSensitiveFileNames();
    }

    // ls host.ReadFile (Go: (string, bool) → Try pattern).
    private bool ReadFileLs(string fileName, out string contents)
    {
        if (host is ILsSourceMapHost sourceMapHost)
        {
            return sourceMapHost.ReadFile(fileName, out contents);
        }
        return program.Host().FS().TryReadFile(fileName, out contents!);
    }

    // languageservice.go:40-42 — l.toPath (the canonical LanguageService.ToPath; Go
    // returns tspath.ToPath directly, and rides l.UseCaseSensitiveFileNames above).
    internal global::Ts.TsPath.Path ToPath(string fileName) =>
        global::Ts.TsPath.TsPath.ToPath(fileName, program.GetCurrentDirectory(), UseCaseSensitiveFileNames());

    // host.go — l.GetDocumentPositionMapper(generatedFileName): builds the mapper with the
    // lsp session's source-map line data; the adapter here rides the Program host (see the
    // file header).
    private global::Ts.SourceMap.DocumentPositionMapper GetDocumentPositionMapper(string declarationFileName) =>
        global::Ts.SourceMap.SourceMapper.GetDocumentPositionMapper(
            new LsSourceMapHostAdapter(this), declarationFileName)
        ?? throw new InvalidOperationException("no source map data for " + declarationFileName);

    private sealed class LsSourceMapHostAdapter : global::Ts.SourceMap.Host
    {
        private readonly LanguageService l;

        public LsSourceMapHostAdapter(LanguageService l)
        {
            this.l = l;
        }

        public bool UseCaseSensitiveFileNames() => l.UseCaseSensitiveFileNames();

        // PORT-NOTE: see the file header — the lsp session's per-file line maps are not
        // wired yet; DocumentPositionMapper null-checks every ECMALineInfo use.
        public global::Ts.SourceMap.ECMALineInfo? GetECMALineInfo(string fileName) => null;

        public bool ReadFile(string fileName, out string contents) => l.ReadFileLs(fileName, out contents);
    }
}
