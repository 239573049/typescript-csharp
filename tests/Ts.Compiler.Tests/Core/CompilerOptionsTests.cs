using System;
using Ts.Collections;
using Ts.Core;
using Xunit;
using CoreApi = Ts.Core.Core; // Ts.Core.Core class is shadowed by the Ts.Core namespace here.

namespace Ts.Core.Tests;

// No Go-side tests exist for the CompilerOptions family (internal/core has only
// bfs_test.go and pattern_test.go). These are new table-driven tests for the computed
// getters — primarily the GetEmitScriptTarget/GetEmitModuleKind default derivation chains
// — mirroring the Go control flow in compileroptions.go.
public class CompilerOptionsTests
{
    // ---- GetEmitScriptTarget ---------------------------------------------------------

    [Fact]
    public void GetEmitScriptTarget_DefaultsToLatestStandard()
    {
        Assert.Equal(ScriptTarget.LatestStandard, new CompilerOptions().GetEmitScriptTarget());
        Assert.Equal(ScriptTarget.ES2025, ScriptTarget.LatestStandard); // pin the alias value
    }

    [Fact]
    public void GetEmitScriptTarget_ExplicitTargetPassesThrough()
    {
        Assert.Equal(ScriptTarget.ES2017, new CompilerOptions { Target = ScriptTarget.ES2017 }.GetEmitScriptTarget());
    }

    // ---- GetEmitModuleKind derivation chain ------------------------------------------

    [Theory]
    // Explicit module wins.
    [InlineData(ModuleKind.CommonJS, ScriptTarget.ESNext, ModuleKind.CommonJS)]
    [InlineData(ModuleKind.ESNext, ScriptTarget.None, ModuleKind.ESNext)]
    [InlineData(ModuleKind.Preserve, ScriptTarget.None, ModuleKind.Preserve)]
    // Derived from target when module is None.
    [InlineData(ModuleKind.None, ScriptTarget.ESNext, ModuleKind.ESNext)]
    [InlineData(ModuleKind.None, ScriptTarget.ES2022, ModuleKind.ES2022)]
    [InlineData(ModuleKind.None, ScriptTarget.ES2023, ModuleKind.ES2022)]
    [InlineData(ModuleKind.None, ScriptTarget.ES2020, ModuleKind.ES2020)]
    [InlineData(ModuleKind.None, ScriptTarget.ES2021, ModuleKind.ES2020)]
    [InlineData(ModuleKind.None, ScriptTarget.ES2015, ModuleKind.ES2015)]
    [InlineData(ModuleKind.None, ScriptTarget.ES2016, ModuleKind.ES2015)]
    [InlineData(ModuleKind.None, ScriptTarget.ES5, ModuleKind.CommonJS)]
    // Target None → GetEmitScriptTarget = ES2025 (>= ES2022) → ES2022.
    [InlineData(ModuleKind.None, ScriptTarget.None, ModuleKind.ES2022)]
    public void GetEmitModuleKind_DerivationChain(ModuleKind module, ScriptTarget target, ModuleKind expected)
    {
        Assert.Equal(expected, new CompilerOptions { Module = module, Target = target }.GetEmitModuleKind());
    }

    // ---- GetModuleResolutionKind -----------------------------------------------------

    [Theory]
    // Explicit resolution (not Unknown/Classic/Node10) passes through.
    [InlineData(ModuleResolutionKind.Bundler, ModuleKind.CommonJS, ModuleResolutionKind.Bundler)]
    [InlineData(ModuleResolutionKind.Node16, ModuleKind.CommonJS, ModuleResolutionKind.Node16)]
    [InlineData(ModuleResolutionKind.NodeNext, ModuleKind.CommonJS, ModuleResolutionKind.NodeNext)]
    // Unknown/Classic/Node10 derive from the emit module kind.
    [InlineData(ModuleResolutionKind.Unknown, ModuleKind.Node16, ModuleResolutionKind.Node16)]
    [InlineData(ModuleResolutionKind.Unknown, ModuleKind.Node18, ModuleResolutionKind.Node16)]
    [InlineData(ModuleResolutionKind.Unknown, ModuleKind.Node20, ModuleResolutionKind.Node16)]
    [InlineData(ModuleResolutionKind.Unknown, ModuleKind.NodeNext, ModuleResolutionKind.NodeNext)]
    [InlineData(ModuleResolutionKind.Classic, ModuleKind.CommonJS, ModuleResolutionKind.Bundler)]
    [InlineData(ModuleResolutionKind.Node10, ModuleKind.ESNext, ModuleResolutionKind.Bundler)]
    public void GetModuleResolutionKind_DerivationChain(ModuleResolutionKind moduleResolution, ModuleKind module, ModuleResolutionKind expected)
    {
        Assert.Equal(expected, new CompilerOptions { ModuleResolution = moduleResolution, Module = module }.GetModuleResolutionKind());
    }

    // ---- GetEmitModuleDetectionKind --------------------------------------------------

    [Theory]
    [InlineData(ModuleDetectionKind.Legacy, ModuleKind.None, ModuleDetectionKind.Legacy)]
    [InlineData(ModuleDetectionKind.None, ModuleKind.Node16, ModuleDetectionKind.Force)]
    [InlineData(ModuleDetectionKind.None, ModuleKind.NodeNext, ModuleDetectionKind.Force)]
    [InlineData(ModuleDetectionKind.None, ModuleKind.CommonJS, ModuleDetectionKind.Auto)]
    [InlineData(ModuleDetectionKind.None, ModuleKind.ESNext, ModuleDetectionKind.Auto)]
    public void GetEmitModuleDetectionKind_DerivationChain(ModuleDetectionKind moduleDetection, ModuleKind module, ModuleDetectionKind expected)
    {
        Assert.Equal(expected, new CompilerOptions { ModuleDetection = moduleDetection, Module = module }.GetEmitModuleDetectionKind());
    }

    // ---- GetResolveJsonModule --------------------------------------------------------

    [Theory]
    [InlineData(Tristate.True, ModuleKind.CommonJS, true)]
    [InlineData(Tristate.False, ModuleKind.Node20, false)]
    [InlineData(Tristate.Unknown, ModuleKind.Node20, true)]
    [InlineData(Tristate.Unknown, ModuleKind.NodeNext, true)]
    [InlineData(Tristate.Unknown, ModuleKind.Node16, false)]   // resolution Node16, not Bundler
    [InlineData(Tristate.Unknown, ModuleKind.ESNext, true)]    // resolution Bundler
    public void GetResolveJsonModule_DerivationChain(Tristate resolveJsonModule, ModuleKind module, bool expected)
    {
        Assert.Equal(expected, new CompilerOptions { ResolveJsonModule = resolveJsonModule, Module = module }.GetResolveJsonModule());
    }

    // ---- Class-fields derivation -----------------------------------------------------

    [Theory]
    [InlineData(Tristate.True, ScriptTarget.None, true)]
    [InlineData(Tristate.False, ScriptTarget.None, false)]
    [InlineData(Tristate.Unknown, ScriptTarget.ES2022, true)]
    [InlineData(Tristate.Unknown, ScriptTarget.ES2021, false)]
    [InlineData(Tristate.Unknown, ScriptTarget.None, true)] // ES2025 >= ES2022
    public void GetUseDefineForClassFields_DerivationChain(Tristate useDefine, ScriptTarget target, bool expected)
    {
        Assert.Equal(expected, new CompilerOptions { UseDefineForClassFields = useDefine, Target = target }.GetUseDefineForClassFields());
    }

    [Theory]
    [InlineData(Tristate.False, ScriptTarget.ES2022, false)]      // != False fails
    [InlineData(Tristate.Unknown, ScriptTarget.ES2022, true)]
    [InlineData(Tristate.True, ScriptTarget.ES2021, false)]       // target too low
    [InlineData(Tristate.Unknown, ScriptTarget.None, true)]       // ES2025 >= ES2022
    public void GetEmitStandardClassFields_DerivationChain(Tristate useDefine, ScriptTarget target, bool expected)
    {
        Assert.Equal(expected, new CompilerOptions { UseDefineForClassFields = useDefine, Target = target }.GetEmitStandardClassFields());
    }

    // ---- Declaration emit ------------------------------------------------------------

    [Theory]
    [InlineData(Tristate.True, Tristate.Unknown, true)]
    [InlineData(Tristate.Unknown, Tristate.True, true)]   // composite implies declarations
    [InlineData(Tristate.Unknown, Tristate.Unknown, false)]
    public void GetEmitDeclarations_DerivationChain(Tristate declaration, Tristate composite, bool expected)
    {
        Assert.Equal(expected, new CompilerOptions { Declaration = declaration, Composite = composite }.GetEmitDeclarations());
    }

    [Theory]
    [InlineData(Tristate.True, Tristate.True, true)]
    [InlineData(Tristate.True, Tristate.Unknown, false)]  // no declarations emitted
    [InlineData(Tristate.Unknown, Tristate.True, false)]  // map off
    public void GetAreDeclarationMapsEnabled_DerivationChain(Tristate declarationMap, Tristate declaration, bool expected)
    {
        Assert.Equal(expected, new CompilerOptions { DeclarationMap = declarationMap, Declaration = declaration }.GetAreDeclarationMapsEnabled());
    }

    // ---- HasJsonModuleEmitEnabled ----------------------------------------------------

    [Theory]
    [InlineData(ModuleKind.System, false)]
    [InlineData(ModuleKind.UMD, false)]
    [InlineData(ModuleKind.CommonJS, true)]
    [InlineData(ModuleKind.ESNext, true)]
    public void HasJsonModuleEmitEnabled_DerivationChain(ModuleKind module, bool expected)
    {
        Assert.Equal(expected, new CompilerOptions { Module = module }.HasJsonModuleEmitEnabled());
    }

    // ---- Boolean flag getters --------------------------------------------------------

    [Theory]
    [InlineData(Tristate.True, Tristate.Unknown, true)]
    [InlineData(Tristate.Unknown, Tristate.True, true)]
    [InlineData(Tristate.Unknown, Tristate.Unknown, false)]
    public void GetIsolatedModules_DerivationChain(Tristate isolatedModules, Tristate verbatim, bool expected)
    {
        Assert.Equal(expected, new CompilerOptions { IsolatedModules = isolatedModules, VerbatimModuleSyntax = verbatim }.GetIsolatedModules());
    }

    [Theory]
    [InlineData(Tristate.True, Tristate.Unknown, true)]
    [InlineData(Tristate.Unknown, Tristate.True, true)]   // isolated modules preserve const enums
    [InlineData(Tristate.Unknown, Tristate.Unknown, false)]
    public void ShouldPreserveConstEnums_DerivationChain(Tristate preserveConstEnums, Tristate isolatedModules, bool expected)
    {
        Assert.Equal(expected, new CompilerOptions { PreserveConstEnums = preserveConstEnums, IsolatedModules = isolatedModules }.ShouldPreserveConstEnums());
    }

    [Theory]
    [InlineData(Tristate.True, Tristate.Unknown, true)]
    [InlineData(Tristate.Unknown, Tristate.True, true)]
    [InlineData(Tristate.Unknown, Tristate.Unknown, false)]
    public void IsIncremental_DerivationChain(Tristate incremental, Tristate composite, bool expected)
    {
        Assert.Equal(expected, new CompilerOptions { Incremental = incremental, Composite = composite }.IsIncremental());
    }

    [Theory]
    [InlineData(Tristate.True, Tristate.Unknown, true)]
    [InlineData(Tristate.False, Tristate.True, false)]    // explicit AllowJs=false wins over CheckJs
    [InlineData(Tristate.Unknown, Tristate.True, true)]   // falls back to CheckJs
    [InlineData(Tristate.Unknown, Tristate.Unknown, false)]
    public void GetAllowJS_DerivationChain(Tristate allowJs, Tristate checkJs, bool expected)
    {
        Assert.Equal(expected, new CompilerOptions { AllowJs = allowJs, CheckJs = checkJs }.GetAllowJS());
    }

    [Theory]
    [InlineData(Tristate.True, Tristate.Unknown, true)]
    [InlineData(Tristate.False, Tristate.True, false)]
    [InlineData(Tristate.Unknown, Tristate.Unknown, true)]  // Strict != False
    [InlineData(Tristate.Unknown, Tristate.False, false)]
    [InlineData(Tristate.Unknown, Tristate.True, true)]
    public void GetStrictOptionValue_DerivationChain(Tristate value, Tristate strict, bool expected)
    {
        Assert.Equal(expected, new CompilerOptions { Strict = strict }.GetStrictOptionValue(value));
    }

    [Theory]
    [InlineData(Tristate.True, Tristate.Unknown, true)]
    [InlineData(Tristate.Unknown, Tristate.True, true)]   // rewrite implies importing ts extensions
    [InlineData(Tristate.Unknown, Tristate.Unknown, false)]
    public void GetAllowImportingTsExtensions_DerivationChain(Tristate allowImporting, Tristate rewrite, bool expected)
    {
        Assert.Equal(expected, new CompilerOptions { AllowImportingTsExtensions = allowImporting, RewriteRelativeImportExtensions = rewrite }.GetAllowImportingTsExtensions());
    }

    [Fact]
    public void AllowImportingTsExtensionsFrom_TrueForDeclarationFiles()
    {
        var options = new CompilerOptions();
        Assert.True(options.AllowImportingTsExtensionsFrom("foo.d.ts"));
        Assert.False(options.AllowImportingTsExtensionsFrom("foo.ts"));
    }

    [Theory]
    [InlineData(Tristate.Unknown, true)]  // IsTrueOrUnknown
    [InlineData(Tristate.True, true)]
    [InlineData(Tristate.False, false)]
    public void GetResolvePackageJsonExportsImports(Tristate value, bool expected)
    {
        Assert.Equal(expected, new CompilerOptions { ResolvePackageJsonExports = value }.GetResolvePackageJsonExports());
        Assert.Equal(expected, new CompilerOptions { ResolvePackageJsonImports = value }.GetResolvePackageJsonImports());
    }

    // ---- UsesWildcardTypes -----------------------------------------------------------

    [Fact]
    public void UsesWildcardTypes()
    {
        Assert.False(new CompilerOptions().UsesWildcardTypes());               // Types nil
        Assert.False(new CompilerOptions { Types = ["a"] }.UsesWildcardTypes());
        Assert.True(new CompilerOptions { Types = ["*"] }.UsesWildcardTypes());
        Assert.True(new CompilerOptions { Types = ["a", "*"] }.UsesWildcardTypes());
    }

    // ---- GetPathsBasePath ------------------------------------------------------------

    [Fact]
    public void GetPathsBasePath_NullOrEmptyPathsReturnsEmpty()
    {
        Assert.Equal("", new CompilerOptions().GetPathsBasePath("/cwd"));                        // Paths nil
        Assert.Equal("", new CompilerOptions { Paths = new OrderedMap<string, string[]>() }.GetPathsBasePath("/cwd"));
    }

    [Fact]
    public void GetPathsBasePath_UsesPathsBasePathThenCurrentDirectory()
    {
        var paths = new OrderedMap<string, string[]>();
        paths.Set("@app/*", ["src/*"]);
        Assert.Equal("/cwd", new CompilerOptions { Paths = paths }.GetPathsBasePath("/cwd"));
        Assert.Equal("/base", new CompilerOptions { Paths = paths, PathsBasePath = "/base" }.GetPathsBasePath("/cwd"));
    }

    // ---- GetEffectiveTypeRoots -------------------------------------------------------

    [Fact]
    public void GetEffectiveTypeRoots_ExplicitTypeRootsFromConfig()
    {
        string[] roots = ["/types"];
        var (result, fromConfig) = new CompilerOptions { TypeRoots = roots }.GetEffectiveTypeRoots("/cwd");
        Assert.Same(roots, result);
        Assert.True(fromConfig);
    }

    [Fact]
    public void GetEffectiveTypeRoots_WalksAncestorsFromConfigDir()
    {
        var (result, fromConfig) = new CompilerOptions { ConfigFilePath = "/proj/tsconfig.json" }.GetEffectiveTypeRoots("/cwd");
        Assert.False(fromConfig);
        Assert.NotEmpty(result);
        Assert.Equal("/proj/node_modules/@types", result[0]);
    }

    [Fact]
    public void GetEffectiveTypeRoots_UsesCurrentDirectoryWhenNoConfig()
    {
        var (result, fromConfig) = new CompilerOptions().GetEffectiveTypeRoots("/cwd");
        Assert.False(fromConfig);
        Assert.Equal("/cwd/node_modules/@types", result[0]);
    }

    [Fact]
    public void GetEffectiveTypeRoots_ThrowsWhenNoConfigAndNoCurrentDirectory()
    {
        Assert.Throws<InvalidOperationException>(() => new CompilerOptions().GetEffectiveTypeRoots(""));
    }

    // ---- Clone (shallow) -------------------------------------------------------------

    [Fact]
    public void Clone_IsShallowCopy()
    {
        var paths = new OrderedMap<string, string[]>();
        var original = new CompilerOptions
        {
            Target = ScriptTarget.ES2020,
            Strict = Tristate.True,
            OutDir = "dist",
            Lib = ["lib.es2020.d.ts"],
            Paths = paths,
            MaxNodeModuleJsDepth = 3,
        };

        var clone = original.Clone();

        Assert.NotSame(original, clone);
        Assert.Equal(ScriptTarget.ES2020, clone.Target);
        Assert.Equal(Tristate.True, clone.Strict);
        Assert.Equal("dist", clone.OutDir);
        Assert.Equal(3, clone.MaxNodeModuleJsDepth);
        // Reference fields are shared (shallow), matching Go's field-by-field Set.
        Assert.Same(original.Lib, clone.Lib);
        Assert.Same(original.Paths, clone.Paths);

        // Mutating a value field on the clone does not affect the original.
        clone.Target = ScriptTarget.ESNext;
        Assert.Equal(ScriptTarget.ES2020, original.Target);
    }

    // ---- EmptyCompilerOptions & lookup tables ----------------------------------------

    [Fact]
    public void EmptyCompilerOptions_IsZeroValue()
    {
        Assert.NotNull(CoreApi.EmptyCompilerOptions);
        Assert.Equal(ScriptTarget.LatestStandard, CoreApi.EmptyCompilerOptions.GetEmitScriptTarget());
    }

    [Fact]
    public void ModuleKindToModuleResolutionKind_Table()
    {
        Assert.Equal(2, CoreApi.ModuleKindToModuleResolutionKind.Count);
        Assert.Equal(ModuleResolutionKind.Node16, CoreApi.ModuleKindToModuleResolutionKind[ModuleKind.Node16]);
        Assert.Equal(ModuleResolutionKind.NodeNext, CoreApi.ModuleKindToModuleResolutionKind[ModuleKind.NodeNext]);
    }

    [Fact]
    public void ResolutionModeConstantsAliasModuleKind()
    {
        Assert.Equal(ModuleKind.None, CoreApi.ResolutionModeNone);
        Assert.Equal(ModuleKind.CommonJS, CoreApi.ResolutionModeCommonJS);
        Assert.Equal(ModuleKind.ESNext, CoreApi.ResolutionModeESM);
    }

    // ---- ModuleKind extension methods ------------------------------------------------

    [Theory]
    [InlineData(ModuleKind.ES2015, true)]
    [InlineData(ModuleKind.ES2020, true)]
    [InlineData(ModuleKind.ESNext, true)]
    [InlineData(ModuleKind.CommonJS, false)]
    [InlineData(ModuleKind.Node16, false)]
    [InlineData(ModuleKind.Preserve, false)]
    [InlineData(ModuleKind.None, false)]
    public void ModuleKind_IsNonNodeESM(ModuleKind kind, bool expected)
    {
        Assert.Equal(expected, kind.IsNonNodeESM());
    }

    [Theory]
    [InlineData(ModuleKind.Node18, true)]
    [InlineData(ModuleKind.Node20, true)]
    [InlineData(ModuleKind.NodeNext, true)]
    [InlineData(ModuleKind.Preserve, true)]
    [InlineData(ModuleKind.ESNext, true)]
    [InlineData(ModuleKind.Node16, false)]
    [InlineData(ModuleKind.ES2015, false)]
    [InlineData(ModuleKind.CommonJS, false)]
    public void ModuleKind_SupportsImportAttributes(ModuleKind kind, bool expected)
    {
        Assert.Equal(expected, kind.SupportsImportAttributes());
    }

    // ---- ModuleResolutionKind.ToDisplayString (hand-written String) ------------------

    [Theory]
    [InlineData(ModuleResolutionKind.Classic, "Classic")]
    [InlineData(ModuleResolutionKind.Node10, "Node10")]
    [InlineData(ModuleResolutionKind.Node16, "Node16")]
    [InlineData(ModuleResolutionKind.NodeNext, "NodeNext")]
    [InlineData(ModuleResolutionKind.Bundler, "Bundler")]
    public void ModuleResolutionKind_ToDisplayString(ModuleResolutionKind kind, string expected)
    {
        Assert.Equal(expected, kind.ToDisplayString());
    }

    [Fact]
    public void ModuleResolutionKind_ToDisplayString_ThrowsOnZeroValue()
    {
        Assert.Throws<InvalidOperationException>(() => ModuleResolutionKind.Unknown.ToDisplayString());
    }

    // ---- JsxEmit.ToDisplayString (hand-written String) -------------------------------

    [Theory]
    [InlineData(JsxEmit.Preserve, "preserve")]
    [InlineData(JsxEmit.ReactNative, "react-native")]
    [InlineData(JsxEmit.React, "react")]
    [InlineData(JsxEmit.ReactJSX, "react-jsx")]
    [InlineData(JsxEmit.ReactJSXDev, "react-jsxdev")]
    public void JsxEmit_ToDisplayString(JsxEmit jsx, string expected)
    {
        Assert.Equal(expected, jsx.ToDisplayString());
    }

    [Fact]
    public void JsxEmit_ToDisplayString_ThrowsOnZeroValue()
    {
        Assert.Throws<InvalidOperationException>(() => JsxEmit.None.ToDisplayString());
    }

    [Theory]
    [InlineData(JsxEmit.React, true)]
    [InlineData(JsxEmit.ReactJSX, true)]
    [InlineData(JsxEmit.ReactJSXDev, true)]
    [InlineData(JsxEmit.Preserve, false)]
    [InlineData(JsxEmit.None, false)]
    public void GetJSXTransformEnabled(JsxEmit jsx, bool expected)
    {
        Assert.Equal(expected, new CompilerOptions { Jsx = jsx }.GetJSXTransformEnabled());
    }

    // ---- NewLineKind -----------------------------------------------------------------

    [Theory]
    [InlineData("\r\n", NewLineKind.CRLF)]
    [InlineData("\n", NewLineKind.LF)]
    [InlineData("x", NewLineKind.None)]
    public void GetNewLineKind(string input, NewLineKind expected)
    {
        Assert.Equal(expected, CoreApi.GetNewLineKind(input));
    }

    [Theory]
    [InlineData(NewLineKind.CRLF, "\r\n")]
    [InlineData(NewLineKind.LF, "\n")]
    [InlineData(NewLineKind.None, "\n")]
    public void GetNewLineCharacter(NewLineKind kind, string expected)
    {
        Assert.Equal(expected, kind.GetNewLineCharacter());
    }

    // ---- WatchOptions.WatchInterval --------------------------------------------------

    [Fact]
    public void WatchInterval_DefaultsTo2000Milliseconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(2000), ((WatchOptions?)null).WatchInterval());
        Assert.Equal(TimeSpan.FromMilliseconds(2000), new WatchOptions().WatchInterval());
    }

    [Fact]
    public void WatchInterval_UsesExplicitInterval()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), new WatchOptions { Interval = 500 }.WatchInterval());
    }

    // ---- TypeAcquisition.Equals ------------------------------------------------------

    [Fact]
    public void TypeAcquisition_Equals()
    {
        var a = new TypeAcquisition { Enable = Tristate.True, Include = ["a"], Exclude = ["b"] };
        Assert.True(a.Equals(a));                                    // reference identity
        Assert.False(a.Equals(null));
        Assert.True(a.Equals(new TypeAcquisition { Enable = Tristate.True, Include = ["a"], Exclude = ["b"] }));
        Assert.False(a.Equals(new TypeAcquisition { Enable = Tristate.False, Include = ["a"], Exclude = ["b"] }));
        Assert.False(a.Equals(new TypeAcquisition { Enable = Tristate.True, Include = ["x"], Exclude = ["b"] }));
    }

    [Fact]
    public void TypeAcquisition_Equals_TreatsNilAndEmptyArraysEqual()
    {
        // slices.Equal(nil, []) == true in Go.
        var withNil = new TypeAcquisition();
        var withEmpty = new TypeAcquisition { Include = [], Exclude = [] };
        Assert.True(withNil.Equals(withEmpty));
    }

    // ---- ProjectReference resolution -------------------------------------------------

    [Fact]
    public void ResolveConfigFileNameOfProjectReference()
    {
        Assert.Equal("/p/tsconfig.json", CoreApi.ResolveConfigFileNameOfProjectReference("/p/tsconfig.json"));
        Assert.Equal("/p/tsconfig.json", CoreApi.ResolveConfigFileNameOfProjectReference("/p"));
    }

    [Fact]
    public void ResolveProjectReferencePath()
    {
        Assert.Equal("/p/tsconfig.json", CoreApi.ResolveProjectReferencePath(new ProjectReference { Path = "/p" }));
    }
}
