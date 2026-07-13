// Regression tests for ast.GetJSXImplicitImportBase / GetJSXRuntimeImport as consumed by
// compiler/fileloader.go:550-559.

using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Core;
using Ts.Module;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Compiler.Tests;

public sealed class FileLoaderJsxRuntimeTests
{
    private static SourceFile Parse(string text) =>
        global::Ts.Parser.Parser.ParseSourceFile(
            new SourceFileParseOptions
            {
                FileName = "/test.tsx",
                Path = new global::Ts.TsPath.Path("/test.tsx"),
            },
            text,
            ScriptKind.Tsx);

    [Fact]
    public void GetJSXImplicitImportBase_DefaultsToReactForAutomaticMode()
    {
        var options = new CompilerOptions { Jsx = JsxEmit.ReactJSX };

        var importBase = global::Ts.Compiler.FileLoader.GetJSXImplicitImportBase(options, Parse("const x = <div />;"));

        Assert.Equal("react", importBase);
        Assert.Equal("react/jsx-runtime", global::Ts.Compiler.FileLoader.GetJSXRuntimeImport(importBase, options));
    }

    [Fact]
    public void GetJSXImplicitImportBase_LastPragmaWinsOverCompilerOption()
    {
        var options = new CompilerOptions
        {
            Jsx = JsxEmit.ReactJSXDev,
            JsxImportSource = "from-options",
        };
        var file = Parse("/* @jsxImportSource first */\n/* @jsxImportSource second */\nconst x = <div />;");

        var importBase = global::Ts.Compiler.FileLoader.GetJSXImplicitImportBase(options, file);

        Assert.Equal("second", importBase);
        Assert.Equal("second/jsx-dev-runtime", global::Ts.Compiler.FileLoader.GetJSXRuntimeImport(importBase, options));
    }

    [Fact]
    public void GetJSXImplicitImportBase_ClassicRuntimePragmaDisablesImport()
    {
        var options = new CompilerOptions
        {
            Jsx = JsxEmit.ReactJSX,
            JsxImportSource = "from-options",
        };
        var file = Parse("/* @jsxRuntime classic */\n/* @jsxImportSource from-pragma */\nconst x = <div />;");

        var importBase = global::Ts.Compiler.FileLoader.GetJSXImplicitImportBase(options, file);

        Assert.Equal("", importBase);
        Assert.Equal("", global::Ts.Compiler.FileLoader.GetJSXRuntimeImport(importBase, options));
    }

    [Fact]
    public void ResolveImportsAndModuleAugmentations_FollowsSynthesizedRuntimeImport()
    {
        const string currentDirectory = "/src";
        var fs = VfsTestUtil.FromMap(
            new Dictionary<string, string>
            {
                ["/src/app.tsx"] = "export const app = <div />;",
                ["/src/node_modules/react/jsx-runtime.d.ts"] = "export function jsx(type: any, props: any): any;",
            },
            useCaseSensitiveFileNames: true);
        var options = new CompilerOptions
        {
            Jsx = JsxEmit.ReactJSX,
            Module = ModuleKind.CommonJS,
            NoLib = Tristate.True,
            Types = [],
        };
        var host = CompilerHostImpl.NewCompilerHost(currentDirectory, fs, "/lib", extendedConfigCache: null, trace: null);
        var config = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
            options,
            ["/src/app.tsx"],
            new global::Ts.TsPath.ComparePathsOptions
            {
                UseCaseSensitiveFileNames = true,
                CurrentDirectory = currentDirectory,
            });

        var program = new Program(new ProgramOptions
        {
            Host = host,
            Config = config,
            SingleThreaded = Tristate.True,
        });

        var sourceFile = program.GetSourceFile("/src/app.tsx");
        Assert.NotNull(sourceFile);
        var (moduleReference, specifier) = program.GetJSXRuntimeImportSpecifier(sourceFile!.Path());
        Assert.Equal("react/jsx-runtime", moduleReference);
        Assert.NotNull(specifier);
        Assert.Equal("react/jsx-runtime", specifier!.Text());
        Assert.Contains(program.SourceFiles(), file => file.FileName() == "/src/node_modules/react/jsx-runtime.d.ts");

        var resolvedRuntime = program.GetResolvedModules()[sourceFile.Path()]
            .Where(entry => entry.Key.Name == "react/jsx-runtime")
            .Select(entry => entry.Value)
            .Single();
        Assert.True(resolvedRuntime.IsResolved());
        Assert.Equal("/src/node_modules/react/jsx-runtime.d.ts", resolvedRuntime.ResolvedFileName);
    }
}
