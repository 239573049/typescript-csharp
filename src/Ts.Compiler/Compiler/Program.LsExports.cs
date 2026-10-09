// Additive public re-export consumed by the Ts.LanguageService source-map / file-rename
// ports (internal/ls/source_map.go → Ls/SourceMap.cs, internal/ls/file_rename.go →
// Ls/FileRename.cs).
//
// DEVIATION-REGISTER (AGENTS.md §6, logged in the port report): the Go call sites use
// program.IsSourceFromProjectReference (compiler/program.go, exported). The C# Program
// keeps the check on its private ProjectReferenceFileMapper (Program.cs `Mapper`), and
// Program.cs is outside this session's file scope. This is a 1:1 passthrough — no logic
// change, additive file (zero merge surface with concurrent agents). Same convention as
// Checker.LsExports.cs / Checker.FindRefs.LsExports.cs / ModuleSpecifiers.Update.cs.

namespace Ts.Compiler;

public static class ProgramLsExports
{
    // compiler/program.go — IsSourceFromProjectReference (project-reference redirect probe).
    public static bool IsSourceFromProjectReferenceForLs(this global::Ts.Compiler.Program program, global::Ts.TsPath.Path path) =>
        program.IsSourceFromProjectReferenceCore(path);

    // compiler/program.go — RangeResolvedProjectReference (additive passthrough; same
    // deviation-register as above, consumed by the project system's builders).
    public static bool RangeResolvedProjectReferenceForLs(this global::Ts.Compiler.Program program, Func<global::Ts.TsPath.Path, global::Ts.TsOptions.ParsedCommandLine?, global::Ts.TsOptions.ParsedCommandLine, int, bool> f) =>
        program.RangeResolvedProjectReferenceCore(f);

    // compiler/program.go — RangeResolvedProjectReferenceInChildConfig.
    public static bool RangeResolvedProjectReferenceInChildConfigForLs(this global::Ts.Compiler.Program program, global::Ts.TsOptions.ParsedCommandLine? childConfig, Func<global::Ts.TsPath.Path, global::Ts.TsOptions.ParsedCommandLine?, global::Ts.TsOptions.ParsedCommandLine, int, bool> f) =>
        program.RangeResolvedProjectReferenceInChildConfigCore(childConfig, f);
}

public sealed partial class Program
{
    internal bool IsSourceFromProjectReferenceCore(global::Ts.TsPath.Path path) => Mapper.IsSourceFromProjectReference(path);

    internal bool RangeResolvedProjectReferenceCore(Func<global::Ts.TsPath.Path, global::Ts.TsOptions.ParsedCommandLine?, global::Ts.TsOptions.ParsedCommandLine, int, bool> f) =>
        Mapper.RangeResolvedProjectReference(f);

    internal bool RangeResolvedProjectReferenceInChildConfigCore(global::Ts.TsOptions.ParsedCommandLine? childConfig, Func<global::Ts.TsPath.Path, global::Ts.TsOptions.ParsedCommandLine?, global::Ts.TsOptions.ParsedCommandLine, int, bool> f) =>
        Mapper.RangeResolvedProjectReferenceInChildConfig(childConfig, f);
}
