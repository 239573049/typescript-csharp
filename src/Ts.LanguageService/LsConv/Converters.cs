// Ported from tsgo internal/ls/lsconv/converters.go — the Converters position/range codec
// (:23-225). PORT-NOTE: positions in this port are UTF-16 code units natively (PositionMap was
// deleted, plan/01), so Go's UTF-8↔UTF-16 rescans collapse: for the default UTF-16 encoding
// (and ASCII-only files) character == offset - lineStart. A negotiated UTF-8 position encoding
// would need byte math; that lands with the lsp-server capability wave. The diagnostic tail
// (DiagnosticToLSPPull/Push :227-349) ships with the diagnostics-publishing wave.

using System;
using Ts.Core;

namespace Ts.Ls.LsConv;

// converters.go:28-31 — Script.
public interface Script
{
    string FileName();

    string Text();
}

// converters.go:23-26 — Converters.
public sealed class Converters
{
    private readonly Func<string, LSPLineMap> getLineMap;
    private readonly string positionEncoding;

    // converters.go:33-38 — NewConverters. (positionEncoding: "utf-16" | "utf-8".)
    public Converters(string positionEncoding, Func<string, LSPLineMap> getLineMap)
    {
        this.getLineMap = getLineMap;
        this.positionEncoding = positionEncoding;
    }

    // converters.go:40-45 — ToLSPRange.
    public global::Ts.LsProto.Range ToLSPRange(Script script, TextRange textRange)
    {
        return new global::Ts.LsProto.Range
        {
            Start = PositionToLineAndCharacter(script, textRange.Pos),
            End = PositionToLineAndCharacter(script, textRange.End),
        };
    }

    // converters.go:47-52 — FromLSPRange.
    public TextRange FromLSPRange(Script script, global::Ts.LsProto.Range textRange)
    {
        return new TextRange(
            LineAndCharacterToPosition(script, textRange.Start),
            LineAndCharacterToPosition(script, textRange.End));
    }

    // converters.go:61-66 — ToLSPLocation.
    public global::Ts.LsProto.Location ToLSPLocation(Script script, TextRange rng)
    {
        return new global::Ts.LsProto.Location
        {
            Uri = FileNameToDocumentURI(script.FileName()),
            Range = ToLSPRange(script, rng),
        };
    }

    // converters.go:144-193 — LineAndCharacterToPosition. (UTF-16 native: no rescan needed for
    // the default encoding; see file header PORT-NOTE.)
    public int LineAndCharacterToPosition(Script script, global::Ts.LsProto.Position lineAndCharacter)
    {
        var lineMap = getLineMap(script.FileName());

        var line = (int)lineAndCharacter.Line;
        var ch = (int)lineAndCharacter.Character;

        var textLen = script.Text().Length;

        // Clamp line to valid range.
        if (line >= lineMap.LineStarts.Count)
        {
            return textLen;
        }

        var start = lineMap.LineStarts[line];

        // Determine the end of this line (start of next line, or end of text).
        int lineEnd;
        if (line + 1 < lineMap.LineStarts.Count)
        {
            lineEnd = lineMap.LineStarts[line + 1];
        }
        else
        {
            lineEnd = textLen;
        }

        return Math.Max(start, Math.Min(start + ch, lineEnd));
    }

    // converters.go:194-225 — PositionToLineAndCharacter.
    public global::Ts.LsProto.Position PositionToLineAndCharacter(Script script, int position)
    {
        position = Math.Max(0, Math.Min(position, script.Text().Length));

        var lineMap = getLineMap(script.FileName());

        var line = lineMap.ComputeIndexOfLineStart(position);
        line = Math.Max(0, Math.Min(line, lineMap.LineStarts.Count - 1));

        // The current line ranges from lineMap.LineStarts[line] (or 0) to lineMap.LineStarts[line+1] (or len(text)).

        var start = lineMap.LineStarts[line];

        var character = position - start;

        return new global::Ts.LsProto.Position
        {
            Line = (uint)line,
            Character = (uint)character,
        };
    }

    // converters.go:68-84 — LanguageKindToScriptKind.
    public static ScriptKind LanguageKindToScriptKind(string languageID)
    {
        switch (languageID)
        {
            case "typescript":
                return ScriptKind.TS;
            case "typescriptreact":
                return ScriptKind.Tsx;
            case "javascript":
                return ScriptKind.JS;
            case "javascriptreact":
                return ScriptKind.Jsx;
            case "json":
                return ScriptKind.Json;
            default:
                return ScriptKind.Unknown;
        }
    }

    // converters.go:86-108 — extraEscapeReplacer.
    // https://github.com/microsoft/vscode-uri/blob/edfdccd976efaf4bb8fdeca87e97c47257721729/src/uri.ts#L455
    private static string ExtraEscape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case ':': sb.Append("%3A"); break;
                case '/': sb.Append("%2F"); break;
                case '?': sb.Append("%3F"); break;
                case '#': sb.Append("%23"); break;
                case '[': sb.Append("%5B"); break;
                case ']': sb.Append("%5D"); break;
                case '@': sb.Append("%40"); break;
                case '!': sb.Append("%21"); break;
                case '$': sb.Append("%24"); break;
                case '&': sb.Append("%26"); break;
                case '\'': sb.Append("%27"); break;
                case '(': sb.Append("%28"); break;
                case ')': sb.Append("%29"); break;
                case '*': sb.Append("%2A"); break;
                case '+': sb.Append("%2B"); break;
                case ',': sb.Append("%2C"); break;
                case ';': sb.Append("%3B"); break;
                case '=': sb.Append("%3D"); break;
                case ' ': sb.Append("%20"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    // converters.go:110-142 — FileNameToDocumentURI.
    public static global::Ts.LsProto.DocumentUri FileNameToDocumentURI(string fileName)
    {
        if (global::Ts.Bundled.Bundled.IsBundled(fileName))
        {
            return new global::Ts.LsProto.DocumentUri(fileName);
        }
        if (global::Ts.TsPath.TsPath.IsDynamicFileName(fileName))
        {
            var cutIdx = fileName.IndexOf('/', 2);
            if (cutIdx < 0)
            {
                throw new InvalidOperationException("invalid file name: " + fileName);
            }
            var scheme = fileName[2..cutIdx];
            var rest = fileName[(cutIdx + 1)..];
            var authIdx = rest.IndexOf('/');
            if (authIdx < 0)
            {
                throw new InvalidOperationException("invalid file name: " + fileName);
            }
            var authority = rest[..authIdx];
            var path = rest[(authIdx + 1)..];
            if (authority == "ts-nul-authority")
            {
                return new global::Ts.LsProto.DocumentUri(scheme + ":" + path);
            }
            return new global::Ts.LsProto.DocumentUri(scheme + "://" + authority + "/" + path);
        }

        global::Ts.TsPath.TsPath.SplitVolumePath(fileName, out var volume, out var restName);
        if (volume.Length != 0)
        {
            volume = "/" + ExtraEscape(volume);
        }

        if (restName.StartsWith("//", StringComparison.Ordinal))
        {
            restName = restName[2..];
        }

        var parts = restName.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = ExtraEscape(Uri.EscapeDataString(parts[i]));
        }

        return new global::Ts.LsProto.DocumentUri("file://" + volume + string.Join("/", parts));
    }
}
