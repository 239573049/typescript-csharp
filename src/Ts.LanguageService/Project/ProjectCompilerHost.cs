// Foundation subset of tsgo internal/project/compilerhost.go.

using System;

namespace Ts.Project;

internal sealed class ProjectCompilerHost :
    global::Ts.Compiler.CompilerHost,
    global::Ts.TsOptions.ParseConfigHost
{
    private readonly string currentDirectory;
    private readonly OverlayFileSystem fileSystem;
    private readonly string defaultLibraryPath;

    internal ProjectCompilerHost(
        string currentDirectory,
        OverlayFileSystem fileSystem,
        string defaultLibraryPath)
    {
        this.currentDirectory = currentDirectory;
        this.fileSystem = fileSystem;
        this.defaultLibraryPath = defaultLibraryPath;
    }

    public global::Ts.Vfs.IFileSystem FS() => fileSystem;

    public string DefaultLibraryPath() => defaultLibraryPath;

    public string GetCurrentDirectory() => currentDirectory;

    public void Trace(global::Ts.Diagnostics.Message msg, params object?[] args)
    {
        _ = msg;
        _ = args;
    }

    public global::Ts.Ast.SourceFile? GetSourceFile(global::Ts.Ast.SourceFileParseOptions opts)
    {
        if (!fileSystem.TryReadFile(opts.FileName, out string text))
        {
            return null;
        }

        global::Ts.Core.ScriptKind scriptKind = fileSystem.TryGetOpenFile(opts.Path, out OpenFileSnapshot openFile)
            ? openFile.ScriptKind
            : global::Ts.Core.CoreUtil.GetScriptKindFromFileName(opts.FileName);
        return global::Ts.Parser.Parser.ParseSourceFile(opts, text, scriptKind);
    }

    // Project-reference graph loading is deliberately outside this foundation
    // pass. This matches the current general CompilerHost fallback, which also
    // returns null until a project system supplies the graph.
    public global::Ts.TsOptions.ParsedCommandLine? GetResolvedProjectReference(
        string fileName,
        global::Ts.TsPath.Path path)
    {
        _ = fileName;
        _ = path;
        return null;
    }
}
