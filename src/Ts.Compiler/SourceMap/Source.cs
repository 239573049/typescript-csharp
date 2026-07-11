// Ported from tsgo internal/sourcemap/source.go

namespace Ts.SourceMap;

// Go: type Source interface { Text() string; FileName() string; ECMALineMap() []core.TextPos }
//
// PORT-NOTE (plan/05 §1.3): kept WITHOUT an `I` prefix, matching the landed convention for
// structural host-like interfaces (ast.SourceFileLike, module.Host, module.Program). This also
// preserves double-repo grep fidelity with the Go name `Source`.
// core.TextPos ([]core.TextPos) dissolves to `int[]` (plan/05 §3.1: every position is a
// UTF-16 code-unit offset). Matches the landed ast.SourceFileLike.ECMALineMap() shape.
public interface Source
{
    string Text();

    string FileName();

    int[] ECMALineMap();
}
