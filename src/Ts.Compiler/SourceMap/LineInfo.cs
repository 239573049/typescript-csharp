// Ported from tsgo internal/sourcemap/lineinfo.go

namespace Ts.SourceMap;

// Go: type ECMALineInfo struct { text string; lineStarts core.ECMALineStarts }
//
// core.ECMALineStarts ([]core.TextPos) → int[] (plan/05 §3.1: UTF-16 code-unit offsets).
// PORT-NOTE: `ECMA` is kept upper-cased throughout the source-map package to match the
// already-landed spellings (ast.SourceFile.ECMALineMap, CoreUtil.ComputeECMALineStarts,
// Scanner.GetECMALineStarts) rather than the §1.4 `Ecma` casing — landed precedent wins.
public sealed class ECMALineInfo
{
    // Go unexported fields, read across the package (source_mapper.go accesses li.text /
    // li.lineStarts). plan/05 §1.1: package-private-cross-file → internal.
    internal readonly string text;
    internal readonly int[] lineStarts;

    private ECMALineInfo(string text, int[] lineStarts)
    {
        this.text = text;
        this.lineStarts = lineStarts;
    }

    // Go: func CreateECMALineInfo(text string, lineStarts core.ECMALineStarts) *ECMALineInfo
    public static ECMALineInfo CreateECMALineInfo(string text, int[] lineStarts)
    {
        return new ECMALineInfo(text, lineStarts);
    }

    // Go: func (li *ECMALineInfo) LineCount() int
    public int LineCount()
    {
        return lineStarts.Length;
    }

    // Go: func (li *ECMALineInfo) LineText(line int) string
    public string LineText(int line)
    {
        int pos = lineStarts[line];
        int end;
        if (line + 1 < lineStarts.Length)
        {
            end = lineStarts[line + 1];
        }
        else
        {
            end = text.Length;
        }
        return text[pos..end];
    }
}
