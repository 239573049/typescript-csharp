// Ported from tsgo internal/ls/languageservice.go (foundation subset).

using System;
using Ts.Core;
using Ts.Ls.AutoImport;

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

    // languageservice.go:48-50 — UserPreferences returns the active configuration. The
    // config-sync channel (l.activeConfig, fed by the lsp workspace/didChangeConfiguration
    // wave) is not wired yet; defaults stand in until then.
    private global::Ts.Ls.UserPreferences activeConfig = global::Ts.Ls.UserPreferences.NewDefaultUserPreferences();

    public global::Ts.Ls.UserPreferences UserPreferences() => activeConfig;

    // languageservice.go:16 — projectPath. PORT-NOTE: assigned by the project/lsp wave's
    // NewLanguageService(projectPath, ...); empty for the direct program host.
    internal global::Ts.TsPath.Path projectPath;

    // Go: l.FormatOptions() (UserPreferences.FormatCodeSettings). PORT-NOTE: the config-sync
    // channel is not wired yet; defaults stand in until the lsp/config wave.
    public global::Ts.Format.FormatCodeSettings FormatOptions() =>
        global::Ts.Format.FormatCodeSettings.GetDefaultFormatCodeSettings();

    // Go: l.converters (owned by the lsp session). PORT-NOTE: until the lsp-server wave wires a
    // session-owned instance, build a UTF-16 Converters over the program's own files.
    private global::Ts.Ls.LsConv.Converters? converters;

    internal global::Ts.Ls.LsConv.Converters Converters()
    {
        return converters ??= new global::Ts.Ls.LsConv.Converters("utf-16", fileName =>
            global::Ts.Ls.LsConv.LineMap.ComputeLSPLineStarts(program.GetSourceFile(fileName)!.Text()));
    }

    // languageservice.go:92-107 — getPreparedAutoImportView returns an auto-import view for the
    // given file if the registry is prepared to provide up-to-date auto-imports for it. If not,
    // it returns ErrNeedsAutoImports. If auto-imports are disabled via user preferences, it
    // returns (nil, nil). PORT-NOTE: a null registry (direct program host, no project layer)
    // also yields (null, null) here — Go's host always supplies a registry and the
    // ErrNeedsAutoImports retry protocol belongs to the lsp-server/project wave.
    internal (global::Ts.Ls.AutoImport.View? View, Exception? Error) GetPreparedAutoImportView(global::Ts.Ast.SourceFile fromFile)
    {
        if (UserPreferences().IncludeCompletionsForModuleExports.IsFalse())
        {
            return (null, null);
        }
        var registry = host.AutoImportRegistry();
        if (registry == null)
        {
            return (null, null);
        }
        if (!registry.IsPreparedForImportingFile(fromFile.FileName(), projectPath, UserPreferences()))
        {
            return (null, ErrNeedsAutoImports);
        }

        var view = global::Ts.Ls.AutoImport.View.NewView(registry, fromFile, projectPath, program, UserPreferences().ModuleSpecifierPreferences());
        return (view, null);
    }

    // languageservice.go:109-118 — getCurrentAutoImportView returns an auto-import view for the
    // given file, based on the current state of the auto-import registry, which may or may not
    // be up-to-date.
    internal global::Ts.Ls.AutoImport.View? GetCurrentAutoImportView(global::Ts.Ast.SourceFile fromFile)
    {
        var registry = host.AutoImportRegistry();
        if (registry == null)
        {
            return null; // PORT-NOTE: see GetPreparedAutoImportView.
        }
        return global::Ts.Ls.AutoImport.View.NewView(
            registry,
            fromFile,
            projectPath,
            program,
            UserPreferences().ModuleSpecifierPreferences());
    }

    // languageservice.go — ErrNeedsAutoImports.
    internal static readonly Exception ErrNeedsAutoImports = new InvalidOperationException("needs auto-imports");

    // languageservice.go:120-123 — used for module specifier completions.
    public bool DirectoryExists(string path)
    {
        return host.DirectoryExists(path);
    }

    // languageservice.go:125-128.
    public string[] ReadDirectory(string path, string[]? extensions, string[]? includes)
    {
        return host.ReadDirectory(program.GetCurrentDirectory(), path, extensions, null /*excludes*/, includes, global::Ts.Vfs.VfsMatch.VfsMatch.UnlimitedDepth);
    }

    // languageservice.go:130-132.
    public string[] GetDirectories(string path)
    {
        return host.GetDirectories(path);
    }

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
