// Ported from tsgo internal/ls/host.go (foundation subset).
//
// The complete Go host also exposes preferences, converters, source-map line data,
// auto-import state, and file-system enumeration. Those members belong to the
// deferred LSP/project/completions layers. This foundation host keeps only the
// Program/checker lifetime surface used by position-based semantic queries.

using System;
using System.Threading;

namespace Ts.Ls;

public interface Host
{
    global::Ts.Compiler.Program GetProgram();

    (global::Ts.Checker.Checker Checker, Action Release) GetTypeCheckerForFile(
        CancellationToken cancellationToken,
        global::Ts.Ast.SourceFile file);

    // host.go:18-24 — used for module specifier completions.
    // ! Do not use for anything else, as this violates the principle that
    // the host is a snapshot-in-time.
    string[] ReadDirectory(string currentDir, string path, string[]? extensions, string[]? excludes, string[]? includes, int depth);

    string[] GetDirectories(string path);

    bool DirectoryExists(string path);

    bool FileExists(string path);
}

public sealed class ProgramHost : Host
{
    private readonly global::Ts.Compiler.Program program;

    public ProgramHost(global::Ts.Compiler.Program program)
    {
        this.program = program ?? throw new ArgumentNullException(nameof(program));
    }

    public global::Ts.Compiler.Program GetProgram() => program;

    public (global::Ts.Checker.Checker Checker, Action Release) GetTypeCheckerForFile(
        CancellationToken cancellationToken,
        global::Ts.Ast.SourceFile file)
    {
        return program.GetTypeCheckerForFile(cancellationToken, file);
    }

    private global::Ts.Vfs.IFileSystem FS() => program.Host().FS();

    public string[] ReadDirectory(string currentDir, string path, string[]? extensions, string[]? excludes, string[]? includes, int depth)
    {
        return global::Ts.Vfs.VfsMatch.VfsMatch.ReadDirectory(FS(), currentDir, path, extensions, excludes, includes, depth).ToArray();
    }

    public string[] GetDirectories(string path)
    {
        return FS().GetAccessibleEntries(path).Directories ?? [];
    }

    public bool DirectoryExists(string path) => FS().DirectoryExists(path);

    public bool FileExists(string path) => FS().FileExists(path);
}
