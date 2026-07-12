// Ported from tsgo internal/ls/languageservice.go (foundation subset).

using System;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    private readonly Host host;
    private readonly global::Ts.Compiler.Program program;

    public LanguageService(Host host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        program = host.GetProgram()!; // Go unconditionally dereferences the host's Program.
    }

    // Go: NewLanguageService(projectPath, program, host, activeFile).
    // The project path, active preferences, converters, and source-map cache are
    // consumed only by deferred LS/LSP features, so the foundation constructor
    // accepts the concrete Program and installs its direct host adapter.
    public static LanguageService NewLanguageService(global::Ts.Compiler.Program program)
    {
        return new LanguageService(new ProgramHost(program));
    }

    public global::Ts.Compiler.Program GetProgram() => program;

    private global::Ts.Ast.SourceFile GetProgramAndFile(string fileName)
    {
        var file = program.GetSourceFile(fileName);
        if (file is null)
        {
            // Go panics here. InvalidOperationException is the repository's panic
            // mapping for a violated language-service request invariant.
            throw new InvalidOperationException("file not found: " + fileName);
        }
        return file;
    }

    private global::Ts.Ast.Node GetNodeAtPosition(
        string fileName,
        int position,
        out global::Ts.Ast.SourceFile file)
    {
        file = GetProgramAndFile(fileName);
        if ((uint)position > (uint)file.End)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        // Positions are already UTF-16 code-unit offsets in C# strings. The LSP
        // line/character converter will remain outside this raw-position API.
        return global::Ts.AstNav.Tokens.GetTouchingPropertyName(file, position);
    }
}
