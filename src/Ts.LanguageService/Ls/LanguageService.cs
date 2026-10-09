// Ported from tsgo internal/ls/languageservice.go.

using System;
using Ts.Core;
using Ts.Ls.AutoImport;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    private readonly Host host;
    private readonly global::Ts.Compiler.Program program;

    // languageservice.go:20 — the resolved client capabilities. PORT-NOTE: Go reads them
    // per request via lsproto.GetClientCapabilities(ctx); plan/01 §4.4 fixed the C#
    // RequestContext shape (no context values), so the session-scoped capabilities are
    // stored on the instance at construction (identical semantics: immutable for the
    // service lifetime; registered deviation, see Project/LsProto.Missing.cs).
    private readonly global::Ts.LsProto.ResolvedClientCapabilities clientCapabilities;

    // languageservice.go — the negotiated position encoding ("utf-16" | "utf-8"); the
    // converters are built over it (ls session.go: NewConverters(positionEncoding, ...)).
    private readonly string positionEncoding;

    public LanguageService(Host host)
        : this(new global::Ts.TsPath.Path(""), host, new global::Ts.LsProto.ResolvedClientCapabilities(), global::Ts.Ls.UserPreferences.NewDefaultUserPreferences(), "utf-16")
    {
    }

    // languageservice.go:24-38 — NewLanguageService(projectPath, program, host, activeFile).
    // Go's host.GetPreferences(activeFile) is the snapshot's per-session preference set; the
    // C# session resolves it to a value before calling this factory, so the activeFile
    // parameter dissolves. The converters come off the host in Go; the C# foundation host
    // carries none, so they are built over the program's own files with the negotiated
    // encoding.
    public LanguageService(
        global::Ts.TsPath.Path projectPath,
        Host host,
        global::Ts.LsProto.ResolvedClientCapabilities clientCapabilities,
        global::Ts.Ls.UserPreferences userPreferences,
        string positionEncoding)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        program = host.GetProgram()!; // Go unconditionally dereferences the host's Program.
        this.projectPath = projectPath;
        this.clientCapabilities = clientCapabilities ?? new global::Ts.LsProto.ResolvedClientCapabilities();
        activeConfig = userPreferences ?? global::Ts.Ls.UserPreferences.NewDefaultUserPreferences();
        this.positionEncoding = positionEncoding;
    }

    // Go: NewLanguageService(projectPath, program, host, activeFile). Foundation entry kept
    // for the direct-program callers (tests, fourslash): default-constructed capabilities,
    // default preferences, UTF-16 encoding, and the program's own host adapter.
    public static LanguageService NewLanguageService(global::Ts.Compiler.Program program)
    {
        return new LanguageService(new ProgramHost(program));
    }

    // languageservice.go:24-38 — NewLanguageService. The lsp-server wave constructs the
    // service with the project path, resolved client capabilities, the session preference
    // set, and the negotiated position encoding.
    public static LanguageService NewLanguageService(
        string projectPath,
        global::Ts.Compiler.Program program,
        Host? host,
        global::Ts.LsProto.ResolvedClientCapabilities clientCapabilities,
        global::Ts.Ls.UserPreferences userPreferences,
        string positionEncoding)
    {
        return new LanguageService(
            global::Ts.TsPath.TsPath.ToPath(projectPath, program.GetCurrentDirectory(), program.UseCaseSensitiveFileNames()),
            host ?? new ProgramHost(program),
            clientCapabilities,
            userPreferences,
            positionEncoding);
    }

    // languageservice.go:20 — projectPath.
    internal global::Ts.TsPath.Path projectPath;

    // The stored resolved client capabilities (see the field comment above).
    internal global::Ts.LsProto.ResolvedClientCapabilities ClientCapabilities() => clientCapabilities;

    public global::Ts.Compiler.Program GetProgram() => program;

    // languageservice.go:48-50 — UserPreferences returns the active configuration. The
    // config-sync channel (l.activeConfig, fed by the lsp workspace/didChangeConfiguration
    // wave) seeds the value at construction (NewLanguageService).
    private global::Ts.Ls.UserPreferences activeConfig;

    public global::Ts.Ls.UserPreferences UserPreferences() => activeConfig;

    // Go: l.FormatOptions() (languageservice.go:52-54 — activeConfig.FormatCodeSettings).
    public global::Ts.Format.FormatCodeSettings FormatOptions() =>
        UserPreferences().FormatCodeSettings;

    // Go: l.converters (owned by the lsp session, built over the negotiated encoding).
    private global::Ts.Ls.LsConv.Converters? converters;

    internal global::Ts.Ls.LsConv.Converters Converters()
    {
        return converters ??= host.Converters() ?? new global::Ts.Ls.LsConv.Converters(positionEncoding, fileName =>
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

    // languageservice.go — ErrNeedsAutoImports. The sentinel carries its own exception type
    // so the lsp server's retry protocol (errors.Is(err, ls.ErrNeedsAutoImports)) can match it.
    internal static readonly Exception ErrNeedsAutoImports = new NeedsAutoImportsException();

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

    // languageservice.go — getProgramAndFile(documentURI): fileName := documentURI.FileName().
    private global::Ts.Ast.SourceFile GetProgramAndFile(global::Ts.LsProto.DocumentUri documentURI) =>
        GetProgramAndFile(global::Ts.LsProto.DocumentUriExtensions.FileName(documentURI));

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

// languageservice.go — the ErrNeedsAutoImports sentinel's type (Go: errors.New("needs auto-imports")).
public sealed class NeedsAutoImportsException : InvalidOperationException
{
    public NeedsAutoImportsException()
        : base("needs auto-imports")
    {
    }
}
