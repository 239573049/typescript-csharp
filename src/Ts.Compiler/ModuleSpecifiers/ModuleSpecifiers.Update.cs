// Additive public re-export consumed by the Ts.LanguageService file-rename port
// (internal/ls/file_rename.go → Ls/FileRename.cs).
//
// DEVIATION-REGISTER (AGENTS.md §6, logged in the port report): the Go call site passes
// `program` as the modulespecifiers.ModuleSpecifierGenerationHost (compiler/program.go
// satisfies the interface structurally). The C# Program already implements every member
// but does not declare the interface, and Program.cs is outside this session's file
// scope. UpdateModuleSpecifier is ported 1:1 here (specifiers.go:1349 +
// getModuleSpecifierWithPreferences), and ProgramModuleSpecifierGenerationHost is a pure
// delegation adapter over the existing public Program members — no logic, no behavior
// change, additive file (zero merge surface with concurrent agents). Same convention as
// Checker.LsExports.cs / Checker.FindRefs.LsExports.cs.

namespace Ts.ModuleSpecifiers;

// specifiers.go:1349 — UpdateModuleSpecifier.
public static class ModuleSpecifiersUpdate
{
    public static string UpdateModuleSpecifier(
        global::Ts.Core.CompilerOptions compilerOptions,
        ModuleSpecifierGenerationHost host,
        SourceFileForSpecifierGeneration importingSourceFile,
        string importingSourceFileName,
        string oldImportSpecifier,
        string toFileName,
        UserPreferences userPreferences,
        ModuleSpecifierOptions options)
    {
        return GetModuleSpecifierWithPreferences(
            compilerOptions,
            host,
            importingSourceFile,
            importingSourceFileName,
            oldImportSpecifier,
            toFileName,
            userPreferences,
            options);
    }

    // specifiers.go:1365 — getModuleSpecifierWithPreferences.
    private static string GetModuleSpecifierWithPreferences(
        global::Ts.Core.CompilerOptions compilerOptions,
        ModuleSpecifierGenerationHost host,
        SourceFileForSpecifierGeneration importingSourceFile, // !!! | FutureSourceFile
        string importingSourceFileName,
        string oldImportSpecifier, // used only in updatingModuleSpecifier
        string toFileName,
        UserPreferences userPreferences,
        ModuleSpecifierOptions options)
    {
        var info = Specifiers.GetInfo(importingSourceFileName, host);
        var modulePaths = Specifiers.GetAllModulePaths(info, toFileName, host, compilerOptions, userPreferences, options);
        var preferences = Specifiers.GetModuleSpecifierPreferences(userPreferences, host, compilerOptions, importingSourceFile, oldImportSpecifier);

        var resolutionMode = options.OverrideImportMode;
        if (resolutionMode == global::Ts.Core.ModuleKind.None)
        {
            resolutionMode = host.GetDefaultResolutionModeForFile(importingSourceFile);
        }

        foreach (var modulePath in modulePaths)
        {
            var firstDefined = Specifiers.TryGetModuleNameAsNodeModule(modulePath, info, importingSourceFile, host, compilerOptions, userPreferences, false /*packageNameOnly*/, options.OverrideImportMode);
            if (firstDefined.Length > 0)
            {
                return firstDefined;
            }
        }

        return Specifiers.GetLocalModuleSpecifier(toFileName, info, compilerOptions, host, resolutionMode, preferences, false);
    }
}

// compiler/program.go — the Program's structural satisfaction of
// modulespecifiers.ModuleSpecifierGenerationHost, spelled as a delegation adapter (see
// the file header). Every member forwards 1:1 to the existing public Program method.
public sealed class ProgramModuleSpecifierGenerationHost : ModuleSpecifierGenerationHost
{
    private readonly global::Ts.Compiler.Program program;

    public ProgramModuleSpecifierGenerationHost(global::Ts.Compiler.Program program)
    {
        this.program = program;
    }

    public string GetCurrentDirectory() => program.GetCurrentDirectory();

    public bool UseCaseSensitiveFileNames() => program.Host().FS().UseCaseSensitiveFileNames();

    public string CommonSourceDirectory() => program.CommonSourceDirectory();

    public global::Ts.Symlinks.KnownSymlinks GetSymlinkCache() => program.GetSymlinkCache();

    public string GetGlobalTypingsCacheLocation() => program.GetGlobalTypingsCacheLocation();

    public global::Ts.TsOptions.SourceOutputAndProjectReference? GetProjectReferenceFromSource(global::Ts.TsPath.Path path) =>
        program.GetProjectReferenceFromSource(path);

    public string[] GetRedirectTargets(global::Ts.TsPath.Path path) => program.GetRedirectTargets(path);

    public string GetSourceOfProjectReferenceIfOutputIncluded(global::Ts.Ast.HasFileName file) =>
        program.GetSourceOfProjectReferenceIfOutputIncluded(file);

    public bool FileExists(string path) => program.FileExists(path);

    public string GetNearestAncestorDirectoryWithPackageJson(string dirname) =>
        program.GetNearestAncestorDirectoryWithPackageJson(dirname);

    public global::Ts.PackageJson.InfoCacheEntry? GetPackageJsonInfo(string pkgJsonPath) =>
        program.GetPackageJsonInfo(pkgJsonPath);

    public global::Ts.Core.ModuleKind GetDefaultResolutionModeForFile(global::Ts.Ast.HasFileName file) =>
        program.GetDefaultResolutionModeForFile(file);

    public global::Ts.Module.ResolvedModule? GetResolvedModuleFromModuleSpecifier(global::Ts.Ast.HasFileName file, global::Ts.Ast.Node? moduleSpecifier) =>
        program.GetResolvedModuleFromModuleSpecifier(file, moduleSpecifier);

    public global::Ts.Core.ModuleKind GetModeForUsageLocation(global::Ts.Ast.HasFileName file, global::Ts.Ast.Node? moduleSpecifier) =>
        program.GetModeForUsageLocation(file, moduleSpecifier);
}
