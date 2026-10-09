// Ported from tsgo internal/lsp/stack_sanitizer.go (whole file).
//
// PORT-NOTE: .NET stack frames have a different shape from Go's (no
// " +0xHEX" PC suffixes, no "in goroutine N" markers, different path
// conventions). The Go algorithm is kept verbatim anyway — the server feeds
// it stack text and the sanitizer only rewrites lines that carry the
// "typescript-go/internal" module marker (kept verbatim as in the source),
// redacting everything else as "(REDACTED FRAME)".

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Ts.Core;

namespace Ts.Lsp;

internal static partial class StackSanitizer
{
    // VS Code's telemetry pipeline redacts any string matching
    // /(key|token|sig|secret|signature|password|passwd|pwd|android:value)[^a-zA-Z0-9]/i
    // as `<REDACTED: Generic Secret>`, which trips on innocuous Go frames like
    // `getSignatureHelp(`. Insert `X_X` after each trigger keyword that we know
    // can appear in our sanitized output, when followed by punctuation we
    // actually emit (`(`, `[`, `.`, `|`); reverse by removing the marker (replace
    // `X_X` with the empty string) on the dashboard.
    // stack_sanitizer.go:17 — genericSecretKeywordRegex.
    [GeneratedRegex(@"(?i)(key|token|signature|sig|pwd)([(\[.|])")]
    private static partial Regex GenericSecretKeywordRegex();

    // stack_sanitizer.go:19-21 — defeatGenericSecretRegex.
    internal static string DefeatGenericSecretRegex(string s)
    {
        return GenericSecretKeywordRegex().Replace(s, "${1}X_X${2}");
    }

    // stack_sanitizer.go:23-62 — sanitizeStackTrace.
    internal static string SanitizeStackTrace(string stack)
    {
        // TODO: should we just look for the first '(' and
        // just strip everything before the prior newline?
        int startIndex = stack.IndexOf("runtime/debug.Stack()", StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return "";
        }
        stack = stack[startIndex..];

        var result = new StringBuilder();

        foreach (var (lineNum, line0) in CoreUtil.Enumerate(Lines(stack)))
        {
            string line = line0;
            if (lineNum > 0)
            {
                result.Append('\n');
            }

            int i = 0;
            // Skip whitespace
            while (i < line.Length)
            {
                if (line[i] != ' ' && line[i] != '\t')
                {
                    break;
                }
                i++;
            }

            result.Append(line[..i]);

            line = line[i..];

            int ourModuleIndex = line.IndexOf("typescript-go/internal", StringComparison.Ordinal);
            if (ourModuleIndex >= 0)
            {
                line = line[ourModuleIndex..];
                WriteSanitizedModuleOrPath(line, result);
            }
            else
            {
                result.Append("(REDACTED FRAME)");
            }
        }

        return DefeatGenericSecretRegex(result.ToString());
    }

    // stack_sanitizer.go:64-96 — writeSanitizedModuleOrPath.
    internal static void WriteSanitizedModuleOrPath(string line, StringBuilder result)
    {
        // We don't expect things like \r, but it doesn't hurt to trim just in case.
        line = line.Trim();

        int plusHex = line.IndexOf(" +0x", StringComparison.Ordinal);
        if (plusHex >= 0)
        {
            line = line[..plusHex];
        }
        else
        {
            int inGoroutine = line.LastIndexOf(" in goroutine ", StringComparison.Ordinal);
            if (inGoroutine >= 0)
            {
                line = line[..inGoroutine];
            }
        }

        foreach (var (segmentIndex, segment0) in CoreUtil.Enumerate(line.Split('/')))
        {
            string segment = segment0;
            if (segmentIndex > 0)
            {
                result.Append("|>");
            }

            // See if the string ends with ), and strip out all the arguments.
            if (segment.EndsWith(")", StringComparison.Ordinal))
            {
                int openParenIndex = segment.LastIndexOf('(');
                if (openParenIndex < 0)
                {
                    // Closing parenthesis, but no opening - bail out.
                    result.Append("???");
                    continue;
                }

                segment = segment[..openParenIndex];
                result.Append(segment);
                result.Append("()");
                continue;
            }

            result.Append(segment);
        }
    }

    // Go: strings.Lines (stdlib, not tsgo code) — yields each line of s including
    // its terminating '\n'; the last line may lack the '\n'; an empty s yields
    // nothing. Local helper because the BCL has no equivalent.
    private static IEnumerable<string> Lines(string s)
    {
        int start = 0;
        while (start < s.Length)
        {
            int newline = s.IndexOf('\n', start);
            if (newline >= 0)
            {
                yield return s[start..(newline + 1)];
                start = newline + 1;
            }
            else
            {
                yield return s[start..];
                yield break;
            }
        }
    }
}
