// Ported from tsgo internal/printer/emithost.go

using Ts.Ast;
using Ts.Core;
using Ts.TsOptions;
using Ts.TsPath;

namespace Ts.Printer;

// NOTE: EmitHost operations must be thread-safe
public interface EmitHost
{
    CompilerOptions? Options();
    SourceFile[] SourceFiles();
    bool UseCaseSensitiveFileNames();
    string GetCurrentDirectory();
    string CommonSourceDirectory();
    bool IsEmitBlocked(string file);

    // PORT-NOTE: Go signature is `WriteFile(fileName, text string) error`. Per 05-conventions.md §4.1
    // the write error crosses ≥2 layers before becoming an emit diagnostic, so it is modeled as an
    // exception (IOException) thrown by the implementation and caught at the emit consumer.
    void WriteFile(string fileName, string text);

    ModuleKind GetEmitModuleFormatOfFile(HasFileName file);
    EmitResolver GetEmitResolver();
    SourceOutputAndProjectReference? GetProjectReferenceFromSource(Path path);
    bool IsSourceFileFromExternalLibrary(SourceFile file);
}
