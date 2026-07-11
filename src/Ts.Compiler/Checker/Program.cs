// Ported from tsgo internal/checker/checker.go:548-574 (Program + Host interfaces).
// This is the interface the Checker consumes; the compiler's concrete Program implements it
// (that port is unlocked by this skeleton). plan/03 §9.1.
//
// §1.5 (namespace shadowing): cross-package class/interface names that equal a sibling
// namespace segment are fully qualified with global::.

using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

// checker.go:548-570
public interface Program : Host
{
    CompilerOptions Options();

    SourceFile[] SourceFiles();

    void BindSourceFiles();

    // FileExists(string) is inherited from ModuleSpecifierGenerationHost (Go dedups the identical
    // Program-interface declaration).

    SourceFile? GetSourceFile(string fileName);

    SourceFile? GetSourceFileForResolvedModule(string fileName);

    ModuleKind GetEmitModuleFormatOfFile(HasFileName sourceFile);

    ResolutionMode GetEmitSyntaxForUsageLocation(HasFileName sourceFile, StringLiteralLikeNode? usageLocation);

    ModuleKind GetImpliedNodeFormatForEmit(HasFileName sourceFile);

    global::Ts.Module.ResolvedModule? GetResolvedModule(HasFileName currentSourceFile, string moduleReference, ResolutionMode mode);

    Dictionary<global::Ts.TsPath.Path, global::Ts.Module.ModeAwareCache<global::Ts.Module.ResolvedModule>> GetResolvedModules();

    Dictionary<string, bool> GetPackagesMap();

    SourceFileMetaData GetSourceFileMetaData(global::Ts.TsPath.Path path);

    (string ModuleReference, Node? Specifier) GetJSXRuntimeImportSpecifier(global::Ts.TsPath.Path path);

    Node? GetImportHelpersImportSpecifier(global::Ts.TsPath.Path path);

    bool SourceFileMayBeEmitted(SourceFile sourceFile, bool forceDtsEmit);

    bool IsSourceFileDefaultLibrary(global::Ts.TsPath.Path path);

    global::Ts.TsOptions.SourceOutputAndProjectReference? GetProjectReferenceFromOutputDts(global::Ts.TsPath.Path path);

    global::Ts.TsOptions.ParsedCommandLine? GetRedirectForResolution(HasFileName file);

    // CommonSourceDirectory() is inherited from ModuleSpecifierGenerationHost (Go dedups the
    // identical Program-interface declaration).
}

// checker.go:572-574
public interface Host : global::Ts.ModuleSpecifiers.ModuleSpecifierGenerationHost
{
}
