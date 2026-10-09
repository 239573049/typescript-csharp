// Ported from tsgo internal/project/snapshot.go (whole file).
// PORT-NOTE (hosted member): Go's ls.Project interface lives in internal/ls/crossproject.go;
// the C# Ts.Ls foundation has not landed crossproject.go, so the interface is hosted here
// and Project implements it. When crossproject.go ports, move it to Ts.Ls.

namespace Ts.Project;

// crossproject.go:17-22 — Project.
public interface LsProject
{
    global::Ts.TsPath.Path Id();

    global::Ts.Compiler.Program? GetProgram();

    bool HasFile(string fileName);
}
