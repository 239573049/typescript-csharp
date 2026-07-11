// Ported from tsgo internal/ast (HasFileName capability interface).
// Minimal-build for the checker skeleton (plan/03 dependency); belongs to Ts.Ast so the
// checker.Program interface and modulespecifiers can reference it. Keeps the Go name for
// grep-fidelity (a capability marker, not a behaviour interface with an I-prefix).

namespace Ts.Ast;

// Go: type HasFileName interface { FileName() string; Path() tspath.Path }
public interface HasFileName
{
    string FileName();

    global::Ts.TsPath.Path Path();
}
