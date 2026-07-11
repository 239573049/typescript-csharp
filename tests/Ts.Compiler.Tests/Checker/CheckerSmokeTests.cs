// Smoke tests for the phase-5 checker skeleton (plan/03). Real check* logic is stubbed, so these
// only assert that the concrete Checker constructs (NewChecker), that basic type interning works,
// and that the checker pool hands out a checker.

using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Checker;
using Ts.Core;
using Xunit;

namespace Ts.Checker.Tests;

public class CheckerSmokeTests
{
    [Fact]
    public void NewChecker_ConstructsWithoutThrowing()
    {
        var program = new FakeProgram();
        var (checker, _) = Checker.NewChecker(program, null);

        Assert.NotNull(checker);
        // Intrinsic types were created during construction.
        Assert.NotNull(checker.anyType);
        Assert.NotNull(checker.stringType);
        Assert.NotNull(checker.booleanType);
        Assert.NotNull(checker.emptyObjectType);
        Assert.True(checker.anyType!.Flags.HasFlag(TypeFlags.Any));
    }

    [Fact]
    public void StringLiteralTypes_AreInterned()
    {
        var program = new FakeProgram();
        var (checker, _) = Checker.NewChecker(program, null);

        var a = checker.GetStringLiteralType("hello");
        var b = checker.GetStringLiteralType("hello");
        var c = checker.GetStringLiteralType("world");

        Assert.Same(a, b);
        Assert.NotSame(a, c);
        Assert.True(a.IsStringLiteral());
        Assert.Equal("hello", a.AsLiteralType().Value());
    }

    [Fact]
    public void NumberLiteralTypes_AreInterned_AndNaNIsSeparate()
    {
        var program = new FakeProgram();
        var (checker, _) = Checker.NewChecker(program, null);

        var one = checker.GetNumberLiteralType(new Ts.JsNum.Number(1));
        var oneAgain = checker.GetNumberLiteralType(new Ts.JsNum.Number(1));
        var nan1 = checker.GetNumberLiteralType(new Ts.JsNum.Number(double.NaN));
        var nan2 = checker.GetNumberLiteralType(new Ts.JsNum.Number(double.NaN));

        Assert.Same(one, oneAgain);
        Assert.Same(nan1, nan2); // NaN routed through the dedicated nanType field
        Assert.NotSame(one, nan1);
    }

    [Fact]
    public void NewObjectType_SelectsConcreteClassByObjectFlags()
    {
        var program = new FakeProgram();
        var (checker, _) = Checker.NewChecker(program, null);

        var iface = checker.NewObjectType(ObjectFlags.Interface, null);
        var reference = checker.NewObjectType(ObjectFlags.Reference, null);
        var anon = checker.NewObjectType(ObjectFlags.Anonymous, null);

        Assert.IsType<InterfaceType>(iface);
        Assert.IsType<TypeReference>(reference);
        Assert.IsType<ObjectType>(anon); // exact type, not a subclass
    }

    [Fact]
    public void CheckerPool_GetChecker_ReturnsChecker()
    {
        var program = new FakeProgram();
        var pool = new CheckerPool(program, singleThreaded: true);

        var (checker, release) = pool.GetChecker(CancellationToken.None, null);
        Assert.NotNull(checker);
        release();
    }
}

// Minimal Program implementation: only Options/SourceFiles/BindSourceFiles are exercised by the
// skeleton; the rest throw (they are never reached during construction).
internal sealed class FakeProgram : Program
{
    private readonly CompilerOptions options = new();

    // checker.Program
    public CompilerOptions Options() => options;

    public SourceFile[] SourceFiles() => [];

    public void BindSourceFiles()
    {
    }

    public SourceFile? GetSourceFile(string fileName) => throw new System.NotImplementedException();

    public SourceFile? GetSourceFileForResolvedModule(string fileName) => throw new System.NotImplementedException();

    public ModuleKind GetEmitModuleFormatOfFile(HasFileName sourceFile) => throw new System.NotImplementedException();

    public ModuleKind GetEmitSyntaxForUsageLocation(HasFileName sourceFile, StringLiteralLikeNode? usageLocation) => throw new System.NotImplementedException();

    public ModuleKind GetImpliedNodeFormatForEmit(HasFileName sourceFile) => throw new System.NotImplementedException();

    public Ts.Module.ResolvedModule? GetResolvedModule(HasFileName currentSourceFile, string moduleReference, ModuleKind mode) => throw new System.NotImplementedException();

    public Dictionary<Ts.TsPath.Path, Ts.Module.ModeAwareCache<Ts.Module.ResolvedModule>> GetResolvedModules() => throw new System.NotImplementedException();

    public Dictionary<string, bool> GetPackagesMap() => throw new System.NotImplementedException();

    public SourceFileMetaData GetSourceFileMetaData(Ts.TsPath.Path path) => throw new System.NotImplementedException();

    public (string ModuleReference, Node? Specifier) GetJSXRuntimeImportSpecifier(Ts.TsPath.Path path) => throw new System.NotImplementedException();

    public Node? GetImportHelpersImportSpecifier(Ts.TsPath.Path path) => throw new System.NotImplementedException();

    public bool SourceFileMayBeEmitted(SourceFile sourceFile, bool forceDtsEmit) => throw new System.NotImplementedException();

    public bool IsSourceFileDefaultLibrary(Ts.TsPath.Path path) => throw new System.NotImplementedException();

    public Ts.TsOptions.SourceOutputAndProjectReference? GetProjectReferenceFromOutputDts(Ts.TsPath.Path path) => throw new System.NotImplementedException();

    public Ts.TsOptions.ParsedCommandLine? GetRedirectForResolution(HasFileName file) => throw new System.NotImplementedException();

    // modulespecifiers.ModuleSpecifierGenerationHost (via Host)
    public Ts.Symlinks.KnownSymlinks GetSymlinkCache() => throw new System.NotImplementedException();

    public string CommonSourceDirectory() => throw new System.NotImplementedException();

    public string GetGlobalTypingsCacheLocation() => throw new System.NotImplementedException();

    public bool UseCaseSensitiveFileNames() => throw new System.NotImplementedException();

    public string GetCurrentDirectory() => throw new System.NotImplementedException();

    public Ts.TsOptions.SourceOutputAndProjectReference? GetProjectReferenceFromSource(Ts.TsPath.Path path) => throw new System.NotImplementedException();

    public string[] GetRedirectTargets(Ts.TsPath.Path path) => throw new System.NotImplementedException();

    public string GetSourceOfProjectReferenceIfOutputIncluded(HasFileName file) => throw new System.NotImplementedException();

    public bool FileExists(string path) => throw new System.NotImplementedException();

    public string GetNearestAncestorDirectoryWithPackageJson(string dirname) => throw new System.NotImplementedException();

    public Ts.PackageJson.InfoCacheEntry? GetPackageJsonInfo(string pkgJsonPath) => throw new System.NotImplementedException();

    public ModuleKind GetDefaultResolutionModeForFile(HasFileName file) => throw new System.NotImplementedException();

    public Ts.Module.ResolvedModule? GetResolvedModuleFromModuleSpecifier(HasFileName file, StringLiteralLikeNode? moduleSpecifier) => throw new System.NotImplementedException();

    public ModuleKind GetModeForUsageLocation(HasFileName file, StringLiteralLikeNode? moduleSpecifier) => throw new System.NotImplementedException();
}
