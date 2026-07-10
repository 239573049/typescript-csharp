using System;
using System.Text;

namespace Ts.StringUtil;

// Go: internal/stringutil/js_case.go
//
// InvariantGlobalization audit (plan/07-aot-constraints.md §7): ToLowerJS/ToUpperJS
// are the repo's only legal JS-observable case conversions. They read the
// transcribed Unicode tables (JsCase.g.cs) rather than char.ToUpper/ToLower so the
// result matches String.prototype.toLowerCase/toUpperCase code point for code
// point regardless of the runtime's own Unicode tables.
public static partial class StringUtil
{
    public static string ToLowerJS(string str)
    {
        if (TryToLowerAscii(str, out string ascii))
        {
            return ascii;
        }

        var builder = new StringBuilder(str.Length);
        // casedBefore tracks whether the most recent non-Case_Ignorable code point is
        // "cased", which is the backward half of the Final_Sigma context. We
        // accumulate it as we stream so we never have to scan (or decode) backwards.
        bool casedBefore = false;
        for (int i = 0; i < str.Length;)
        {
            var (r, size) = DecodeJSStringRune(str, i);
            i += size;
            if (IsSurrogate(r))
            {
                // A lone surrogate has no case mapping; preserve it verbatim, matching
                // String.prototype.toLowerCase. (Go had to re-emit the WTF-8 sentinel
                // bytes here because WriteRune would have produced U+FFFD; in UTF-16
                // the code unit is appended directly.)
                builder.Append((char)r);
            }
            else if (TryGetSpecialCasing(r, out int index))
            {
                if (UnicodeTables.SpecialCasingConditions[index] == (byte)SpecialCasingCondition.FinalSigma
                    && IsFinalSigmaContext(casedBefore, str, i))
                {
                    builder.Append(UnicodeTables.SpecialCasingConditionalLower[index]);
                }
                else
                {
                    builder.Append(UnicodeTables.SpecialCasingLower[index]);
                }
            }
            else
            {
                AppendRune(builder, r);
            }
            if (!IsUnicodeCaseIgnorable(r))
            {
                casedBefore = IsSigmaCased(r);
            }
        }
        return builder.ToString();
    }

    public static string ToUpperJS(string str)
    {
        if (TryToUpperAscii(str, out string ascii))
        {
            return ascii;
        }

        var builder = new StringBuilder(str.Length);
        for (int i = 0; i < str.Length;)
        {
            var (r, size) = DecodeJSStringRune(str, i);
            if (IsSurrogate(r))
            {
                // A lone surrogate has no case mapping; preserve it verbatim, matching
                // String.prototype.toUpperCase. (Go copied the sentinel bytes directly
                // because WriteRune would have produced U+FFFD.)
                builder.Append((char)r);
            }
            else if (TryGetSpecialCasing(r, out int index))
            {
                builder.Append(UnicodeTables.SpecialCasingUpper[index]);
            }
            else
            {
                AppendRune(builder, r);
            }
            i += size;
        }

        return builder.ToString();
    }

    // Go: specialCasingMappings[r] map lookup → binary search over the parallel
    // arrays of JsCase.g.cs (plan/04-codegen.md §6.2).
    private static bool TryGetSpecialCasing(int r, out int index)
    {
        index = UnicodeTables.SpecialCasingKeys.BinarySearch((uint)r);
        return index >= 0;
    }

    // Go: toLowerASCII — (string, ok) comma-ok return → Try pattern
    // (plan/05-conventions.md §4).
    private static bool TryToLowerAscii(string str, out string result)
    {
        bool needsMapping = false;
        foreach (char ch in str)
        {
            if (ch >= 0x80)
            {
                result = "";
                return false;
            }
            needsMapping = needsMapping || ('A' <= ch && ch <= 'Z');
        }
        if (!needsMapping)
        {
            result = str;
            return true;
        }

        result = string.Create(str.Length, str, static (buf, s) =>
        {
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                buf[i] = 'A' <= ch && ch <= 'Z' ? (char)(ch + ('a' - 'A')) : ch;
            }
        });
        return true;
    }

    private static bool TryToUpperAscii(string str, out string result)
    {
        bool needsMapping = false;
        foreach (char ch in str)
        {
            if (ch >= 0x80)
            {
                result = "";
                return false;
            }
            needsMapping = needsMapping || ('a' <= ch && ch <= 'z');
        }
        if (!needsMapping)
        {
            result = str;
            return true;
        }

        result = string.Create(str.Length, str, static (buf, s) =>
        {
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                buf[i] = 'a' <= ch && ch <= 'z' ? (char)(ch - ('a' - 'A')) : ch;
            }
        });
        return true;
    }

    // isFinalSigmaContext reports whether a sigma at the current position is in
    // Final_Sigma context: it is preceded by a cased code point and not followed by
    // one. casedBefore carries the backward half (tracked incrementally by the
    // caller so we never scan backwards); afterOffset is the offset just past
    // the sigma, from which we scan forward.
    //
    // ECMAScript points at Unicode Default Case Conversion for toLowerCase, and
    // modern V8 reaches that behavior through Intl::ConvertToLower, which uses
    // ICU root-locale lowercasing for non-Latin1 strings like Greek sigma.
    // We intentionally do not delegate this to golang.org/x/text/cases: x/text
    // is a general Unicode casing library, but its root-locale behavior is not
    // an exact match for the JS semantics exercised by String.prototype
    // .toLowerCase(), especially around Final_Sigma context. TypeScript needs the
    // JS behavior itself here, so we keep the context-sensitive part explicit.
    // SpiderMonkey models Final_Sigma with a more explicit context walk, while
    // Unicode Table 3-17 describes it in terms of Cased and Case_Ignorable.
    // We model the exposed V8/ICU behavior directly here: skip Case_Ignorable code
    // points and then look for a Cased code point, exactly as Unicode Table 3-17
    // defines the Final_Sigma condition. The Cased property already subsumes
    // lowercase, uppercase, and titlecase letters, including the
    // DerivedCoreProperties Lowercase/Uppercase extras such as ª, º, and Roman
    // numerals.
    private static bool IsFinalSigmaContext(bool casedBefore, string str, int afterOffset)
    {
        return casedBefore && !HasSigmaCasedAfter(str, afterOffset);
    }

    private static bool HasSigmaCasedAfter(string str, int start)
    {
        for (int i = start; i < str.Length;)
        {
            var (r, size) = DecodeJSStringRune(str, i);
            i += size;
            if (IsUnicodeCaseIgnorable(r))
            {
                continue;
            }
            return IsSigmaCased(r);
        }
        return false;
    }

    private static bool IsSigmaCased(int r)
    {
        return Unicode.Is(UnicodeTables.CasedR16, UnicodeTables.CasedR32, r);
    }

    private static bool IsUnicodeCaseIgnorable(int r)
    {
        return Unicode.Is(UnicodeTables.CaseIgnorableR16, UnicodeTables.CaseIgnorableR32, r);
    }
}
