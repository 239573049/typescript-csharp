// Ported from tsgo internal/compiler/emitter.go.
//
// This wave wires the real emit pipeline: source file → script transformers → printer → write .js
// (+ source map), and .d.ts via the declaration transformer + EmitResolver. The data contracts
// (EmitOnly, EmitResult, EmitOptions, WriteFile*, SourceMapEmitResult, CombineEmitResults) and the
// Program.Emit orchestration live alongside this file (Emitter.cs data section + Program.Emit.cs).
//
// §1.5 (namespace shadowing): cross-package class names equal to a sibling namespace segment are
// fully qualified with `global::` (transformers → global::Ts.Transformers.*, printer →
// global::Ts.Printer.*, sourcemap → global::Ts.SourceMap.*, stringutil → global::Ts.StringUtil.*,
// tspath → global::Ts.TsPath.*). Host classes without a namespace collision (AstUtilities,
// EsTransformers, ...) are used unqualified where imported.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.Tracing;
using Ts.Transformers;
using Ts.Transformers.Declarations;
using Ts.Transformers.EsTransforms;
using Ts.Transformers.Inliners;
using Ts.Transformers.JsxTransforms;
using Ts.Transformers.ModuleTransforms;
using Ts.Transformers.TsTransforms;

namespace Ts.Compiler;

// emitter.go:24-31
public enum EmitOnly : byte
{
    All = 0,
    Js,
    Dts,
    ForcedDts,
}

// program.go:1605-1610
public sealed class WriteFileData
{
    public int SourceMapUrlPos;
    // PORT-NOTE: Go `BuildInfo any` -> object? (build-info type lands with the incremental wave).
    public object? BuildInfo;
    public Diagnostic[]? Diagnostics;
    public bool SkippedDtsWrite;
}

// program.go:1612
public delegate void WriteFile(string fileName, string text, WriteFileData? data);

// program.go:1614-1618
public sealed class EmitOptions
{
    public SourceFile? TargetSourceFile; // Single file to emit. If null, emits all files
    public EmitOnly EmitOnly;
    public WriteFile? WriteFile;
}

// program.go:1620-1625
public sealed class EmitResult
{
    public bool EmitSkipped;
    public Diagnostic[]? Diagnostics; // Contains declaration emit diagnostics
    public string[]? EmittedFiles; // Array of files the compiler wrote to disk
    public SourceMapEmitResult[]? SourceMaps; // Array of sourceMapData if compiler emitted sourcemaps
}

// program.go:1627-1631
public sealed class SourceMapEmitResult
{
    public string[]? InputSourceFileNames;
    public global::Ts.SourceMap.RawSourceMap? SourceMap;
    public string GeneratedFile = "";
}

// program.go:1697-1713
public static class EmitResults
{
    public static EmitResult CombineEmitResults(IReadOnlyList<EmitResult?> results)
    {
        var result = new EmitResult();
        var diagnostics = new List<Diagnostic>();
        var emittedFiles = new List<string>();
        var sourceMaps = new List<SourceMapEmitResult>();
        foreach (var emitResult in results)
        {
            if (emitResult is null)
            {
                continue; // Skip nil results
            }
            if (emitResult.EmitSkipped)
            {
                result.EmitSkipped = true;
            }
            if (emitResult.Diagnostics is not null)
            {
                diagnostics.AddRange(emitResult.Diagnostics);
            }
            if (emitResult.EmittedFiles is not null)
            {
                emittedFiles.AddRange(emitResult.EmittedFiles);
            }
            if (emitResult.SourceMaps is not null)
            {
                sourceMaps.AddRange(emitResult.SourceMaps);
            }
        }
        result.Diagnostics = diagnostics.Count > 0 ? diagnostics.ToArray() : null;
        result.EmittedFiles = emittedFiles.Count > 0 ? emittedFiles.ToArray() : null;
        result.SourceMaps = sourceMaps.Count > 0 ? sourceMaps.ToArray() : null;
        return result;
    }
}

// emitter.go:33-43
internal sealed class Emitter
{
    internal EmitHost host = null!;
    internal EmitOnly emitOnly;
    internal readonly DiagnosticsCollection emitterDiagnostics = new();
    internal global::Ts.Printer.EmitTextWriter writer = null!;
    internal global::Ts.OutputPaths.OutputPaths paths = null!;
    internal SourceFile sourceFile = null!;
    internal readonly EmitResult emitResult = new();
    internal WriteFile? writeFile;
    internal global::Ts.Tracing.Tracing? tr;

    // emitter.go:45-52
    internal void Emit()
    {
        Action release = tr.Push(global::Ts.Tracing.Phase.Emit, "emit", new Dictionary<string, object?> { ["path"] = sourceFile.Path().Value }, true);
        try
        {
            EmitJSFile(sourceFile, paths.JsFilePath(), paths.SourceMapFilePath());
            EmitDeclarationFile(sourceFile, paths.DeclarationFilePath(), paths.DeclarationMapPath());
            emitResult.Diagnostics = emitterDiagnostics.GetDiagnostics();
        }
        finally
        {
            release();
        }
    }

    // emitter.go:54-57
    private DeclarationTransformer[] GetDeclarationTransformers(global::Ts.Printer.EmitContext emitContext, string declarationFilePath, string declarationMapPath)
    {
        var transform = DeclarationTransformer.NewDeclarationTransformer(host, emitContext, host.Options(), declarationFilePath, declarationMapPath);
        return [transform];
    }

    // emitter.go:59-67
    private SourceFile RunScriptTransformers(global::Ts.Printer.EmitContext emitContext, SourceFile sourceFile)
    {
        Action release = tr.Push(global::Ts.Tracing.Phase.Emit, "transformNodes", new Dictionary<string, object?> { ["path"] = sourceFile.Path().Value }, false);
        try
        {
            foreach (var transformer in GetScriptTransformers(emitContext, host, sourceFile))
            {
                sourceFile = transformer.TransformSourceFile(sourceFile);
            }
            return sourceFile;
        }
        finally
        {
            release();
        }
    }

    // emitter.go:69-79
    private (SourceFile SourceFile, Diagnostic[] Diagnostics) RunDeclarationTransformers(global::Ts.Printer.EmitContext emitContext, SourceFile sourceFile, string declarationFilePath, string declarationMapPath)
    {
        Action release = tr.Push(global::Ts.Tracing.Phase.Emit, "transformNodes", new Dictionary<string, object?> { ["path"] = sourceFile.Path().Value }, false);
        try
        {
            List<Diagnostic>? diags = null;
            foreach (var transformer in GetDeclarationTransformers(emitContext, declarationFilePath, declarationMapPath))
            {
                sourceFile = transformer.TransformSourceFile(sourceFile);
                (diags ??= []).AddRange(transformer.GetDiagnostics());
            }
            return (sourceFile, diags?.ToArray() ?? []);
        }
        finally
        {
            release();
        }
    }

    // emitter.go:81-101
    private static Transformer GetModuleTransformer(TransformOptions opts)
    {
        switch (opts.CompilerOptions!.GetEmitModuleKind())
        {
            case ModuleKind.Preserve:
                // `ESModuleTransformer` contains logic for preserving CJS input syntax in `--module preserve`
                return ESModuleTransformer.NewESModuleTransformer(opts);

            case ModuleKind.ESNext:
            case ModuleKind.ES2022:
            case ModuleKind.ES2020:
            case ModuleKind.ES2015:
            case ModuleKind.Node20:
            case ModuleKind.Node18:
            case ModuleKind.Node16:
            case ModuleKind.NodeNext:
            case ModuleKind.CommonJS:
                return ImpliedModuleTransformer.NewImpliedModuleTransformer(opts);

            default:
                return CommonJSModuleTransformer.NewCommonJSModuleTransformer(opts);
        }
    }

    // emitter.go:103-170
    private static Transformer[] GetScriptTransformers(global::Ts.Printer.EmitContext emitContext, EmitHost host, SourceFile sourceFile)
    {
        var tx = new List<Transformer>();
        var options = host.Options()!;

        // JS files don't use reference calculations as they don't do import elision, no need to calculate it
        bool importElisionEnabled = !options.VerbatimModuleSyntax.IsTrue() && !AstUtilities.IsInJSFile(sourceFile.AsNode());
        bool jsxTransformEnabled = options.GetJSXTransformEnabled() && sourceFile.LanguageVariant == LanguageVariant.Jsx;

        var emitResolver = host.GetEmitResolver();

        global::Ts.Binder.IReferenceResolver referenceResolver;
        if (importElisionEnabled || jsxTransformEnabled || !options.GetIsolatedModules() || options.EmitDecoratorMetadata.IsTrue())
        {
            referenceResolver = emitResolver;
        }
        else
        {
            referenceResolver = global::Ts.Binder.ReferenceResolver.NewReferenceResolver(options, new global::Ts.Binder.ReferenceResolverHooks());
        }

        var opts = new TransformOptions
        {
            Context = emitContext,
            CompilerOptions = options,
            Resolver = referenceResolver,
            EmitResolver = emitResolver,
            GetEmitModuleFormatOfFile = host.GetEmitModuleFormatOfFile,
        };

        // transform TypeScript syntax
        {
            // use type nodes to add metadata decorators
            if (options.EmitDecoratorMetadata.IsTrue())
            {
                tx.Add(MetadataTransformer.NewMetadataTransformer(opts));
            }

            // erase types
            tx.Add(TypeEraserTransformer.NewTypeEraserTransformer(opts));

            // elide imports
            if (importElisionEnabled)
            {
                tx.Add(ImportElisionTransformer.NewImportElisionTransformer(opts));
            }

            // transform `enum`, `namespace`, and parameter properties
            tx.Add(RuntimeSyntaxTransformer.NewRuntimeSyntaxTransformer(opts));

            if (options.ExperimentalDecorators.IsTrue())
            {
                tx.Add(LegacyDecoratorsTransformer.NewLegacyDecoratorsTransformer(opts));
            }
        }

        if (jsxTransformEnabled)
        {
            tx.Add(JSXTransformer.NewJSXTransformer(opts));
        }

        var downleveler = EsTransformers.GetESTransformer(opts);
        if (downleveler is not null)
        {
            tx.Add(downleveler);
        }

        tx.Add(UseStrictTransformer.NewUseStrictTransformer(opts));

        // transform module syntax
        tx.Add(GetModuleTransformer(opts));

        // inlining (formerly done via substitutions)
        if (!options.GetIsolatedModules())
        {
            tx.Add(ConstEnumInliningTransformer.NewConstEnumInliningTransformer(opts));
        }
        return tx.ToArray();
    }

    // emitter.go:172-210
    private void EmitJSFile(SourceFile? sourceFile, string jsFilePath, string sourceMapFilePath)
    {
        var options = host.Options()!;

        if (sourceFile is null || (emitOnly != EmitOnly.All && emitOnly != EmitOnly.Js) || jsFilePath.Length == 0)
        {
            return;
        }

        if (options.NoEmit == Tristate.True || host.IsEmitBlocked(jsFilePath))
        {
            emitResult.EmitSkipped = true;
            return;
        }

        Action release = tr.Push(global::Ts.Tracing.Phase.Emit, "emitJsFileOrBundle", new Dictionary<string, object?> { ["jsFilePath"] = jsFilePath }, true);
        try
        {
            var (emitContext, putEmitContext) = global::Ts.Printer.EmitContext.GetEmitContext();
            try
            {
                sourceFile = RunScriptTransformers(emitContext, sourceFile);

                var printerOptions = new global::Ts.Printer.PrinterOptions
                {
                    RemoveComments = options.RemoveComments.IsTrue(),
                    NewLine = options.NewLine,
                    NoEmitHelpers = options.NoEmitHelpers.IsTrue(),
                    SourceMap = options.SourceMap.IsTrue(),
                    InlineSourceMap = options.InlineSourceMap.IsTrue(),
                    InlineSources = options.InlineSources.IsTrue(),
                    Target = options.Target,
                    // !!!
                };

                // create a printer to print the nodes
                var printer = global::Ts.Printer.Printer.NewPrinter(printerOptions, new global::Ts.Printer.PrintHandlers
                {
                    // !!!
                }, emitContext);

                PrintSourceFile(jsFilePath, sourceMapFilePath, sourceFile, printer, options, ShouldEmitSourceMaps(options, sourceFile));
            }
            finally
            {
                putEmitContext();
            }
        }
        finally
        {
            release();
        }
    }

    // emitter.go:212-270
    private void EmitDeclarationFile(SourceFile? sourceFile, string declarationFilePath, string declarationMapPath)
    {
        var options = host.Options()!;

        if (sourceFile is null || emitOnly == EmitOnly.Js || declarationFilePath.Length == 0)
        {
            return;
        }

        Action release = tr.Push(global::Ts.Tracing.Phase.Emit, "emitDeclarationFileOrBundle", new Dictionary<string, object?> { ["declarationFilePath"] = declarationFilePath }, true);
        try
        {
            var (emitContext, putEmitContext) = global::Ts.Printer.EmitContext.GetEmitContext();
            try
            {
                Diagnostic[] diags;
                (sourceFile, diags) = RunDeclarationTransformers(emitContext, sourceFile, declarationFilePath, declarationMapPath);

                foreach (var elem in diags)
                {
                    // Add declaration transform diagnostics to emit diagnostics
                    emitterDiagnostics.Add(elem);
                }

                if (emitOnly != EmitOnly.ForcedDts && (options.NoEmit == Tristate.True || host.IsEmitBlocked(declarationFilePath)))
                {
                    emitResult.EmitSkipped = true;
                    return;
                }

                bool declBlocked = diags.Length > 0 && emitOnly != EmitOnly.ForcedDts;
                if (declBlocked)
                {
                    emitResult.EmitSkipped = true;
                    return;
                }

                var printerOptions = new global::Ts.Printer.PrinterOptions
                {
                    RemoveComments = options.RemoveComments.IsTrue(),
                    NewLine = options.NewLine,
                    NoEmitHelpers = true,
                    // Module:           options.Module, // NYI
                    // ModuleResolution: options.ModuleResolution, // NYI
                    Target = options.GetEmitScriptTarget(),
                    SourceMap = emitOnly != EmitOnly.ForcedDts && options.DeclarationMap.IsTrue(),
                    InlineSourceMap = options.InlineSourceMap.IsTrue(),
                    // InlineSources:       options.InlineSources.IsTrue(), // ignored, per strada
                    // ExtendedDiagnostics: options.ExtendedDiagnostics.IsTrue(), // NYI
                    OnlyPrintJSDocStyle = true,
                    OmitBraceSourceMapPositions = true,
                };

                // create a printer to print the nodes
                var printer = global::Ts.Printer.Printer.NewPrinter(printerOptions, new global::Ts.Printer.PrintHandlers
                {
                    // !!!
                }, emitContext);

                var declarationMapOptions = new CompilerOptions
                {
                    SourceMap = CoreUtil.IfElse(emitOnly != EmitOnly.ForcedDts && options.DeclarationMap.IsTrue(), Tristate.True, Tristate.False),
                    SourceRoot = options.SourceRoot,
                    MapRoot = options.MapRoot,
                    // Explicitly do not pass through either inline option.
                };
                PrintSourceFile(declarationFilePath, declarationMapPath, sourceFile, printer, declarationMapOptions, ShouldEmitSourceMaps(declarationMapOptions, sourceFile));
            }
            finally
            {
                putEmitContext();
            }
        }
        finally
        {
            release();
        }
    }

    // emitter.go:272-350
    private void PrintSourceFile(string jsFilePath, string sourceMapFilePath, SourceFile sourceFile, global::Ts.Printer.Printer printer, CompilerOptions mapOptions, bool shouldEmitSourceMaps)
    {
        // !!! sourceMapGenerator
        var options = host.Options()!;
        global::Ts.SourceMap.Generator? sourceMapGenerator = null;
        if (shouldEmitSourceMaps)
        {
            sourceMapGenerator = global::Ts.SourceMap.Generator.NewGenerator(
                global::Ts.TsPath.TsPath.GetBaseFileName(global::Ts.TsPath.TsPath.NormalizeSlashes(jsFilePath)),
                GetSourceRoot(mapOptions),
                GetSourceMapDirectory(mapOptions, jsFilePath, sourceFile),
                new global::Ts.TsPath.ComparePathsOptions
                {
                    UseCaseSensitiveFileNames = host.UseCaseSensitiveFileNames(),
                    CurrentDirectory = host.GetCurrentDirectory(),
                });
        }

        printer.Write(sourceFile.AsNode(), sourceFile, writer, sourceMapGenerator);

        int sourceMapUrlPos = -1;
        if (sourceMapGenerator is not null)
        {
            if (mapOptions.SourceMap.IsTrue() || mapOptions.InlineSourceMap.IsTrue())
            {
                emitResult.SourceMaps = AppendSourceMap(emitResult.SourceMaps, new SourceMapEmitResult
                {
                    InputSourceFileNames = sourceMapGenerator.Sources(),
                    SourceMap = sourceMapGenerator.RawSourceMap(),
                    GeneratedFile = jsFilePath,
                });
            }

            var sourceMappingURL = GetSourceMappingURL(
                mapOptions,
                sourceMapGenerator,
                jsFilePath,
                sourceMapFilePath,
                sourceFile);

            if (sourceMappingURL.Length > 0)
            {
                if (!writer.IsAtStartOfLine())
                {
                    writer.RawWrite(CoreUtil.IfElse(options.NewLine == NewLineKind.CRLF, "\r\n", "\n"));
                }
                sourceMapUrlPos = writer.GetTextPos();
                writer.WriteComment("//# sourceMappingURL=");
                writer.WriteComment(sourceMappingURL);
            }

            // Write the source map
            if (sourceMapFilePath.Length > 0)
            {
                var sourceMap = sourceMapGenerator.String();
                if (TryWriteText(sourceMapFilePath, sourceMap, null, out var err))
                {
                    emitResult.EmittedFiles = AppendString(emitResult.EmittedFiles, sourceMapFilePath);
                }
                else
                {
                    emitterDiagnostics.Add(Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Could_not_write_file_0_Colon_1, jsFilePath, err));
                }
            }
        }
        else
        {
            writer.WriteLine();
        }

        // Write the output file
        var text = writer.String();
        if (options.EmitBOM.IsTrue())
        {
            text = global::Ts.StringUtil.StringUtil.AddUTF8ByteOrderMark(text);
        }
        var data = new WriteFileData
        {
            SourceMapUrlPos = sourceMapUrlPos,
            Diagnostics = emitterDiagnostics.GetDiagnostics(),
        };
        bool ok = TryWriteText(jsFilePath, text, data, out var writeErr);
        bool skippedDtsWrite = data.SkippedDtsWrite;
        if (!ok)
        {
            emitterDiagnostics.Add(Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Could_not_write_file_0_Colon_1, jsFilePath, writeErr));
        }
        else if (!skippedDtsWrite)
        {
            emitResult.EmittedFiles = AppendString(emitResult.EmittedFiles, jsFilePath);
        }

        // Reset state
        writer.Clear();
    }

    // emitter.go:352-357 — PORT-NOTE: Go returns error; C# host.WriteFile / writeFile throw IOException,
    // so this returns success + the error text on failure (05-conventions.md §4.1 Try/error-text).
    private bool TryWriteText(string fileName, string text, WriteFileData? data, out string errorText)
    {
        try
        {
            if (writeFile is not null)
            {
                writeFile(fileName, text, data);
            }
            else
            {
                host.WriteFile(fileName, text);
            }
            errorText = "";
            return true;
        }
        catch (System.IO.IOException e)
        {
            errorText = e.Message;
            return false;
        }
    }

    // emitter.go:359-362
    private static bool ShouldEmitSourceMaps(CompilerOptions mapOptions, SourceFile sourceFile)
    {
        return (mapOptions.SourceMap.IsTrue() || mapOptions.InlineSourceMap.IsTrue()) &&
            !global::Ts.TsPath.TsPath.FileExtensionIs(sourceFile.FileName(), global::Ts.TsPath.TsPath.ExtensionJson);
    }

    // emitter.go:364-372
    private static string GetSourceRoot(CompilerOptions mapOptions)
    {
        // Normalize source root and make sure it has trailing "/" so that it can be used to combine paths with the
        // relative paths of the sources list in the sourcemap
        var sourceRoot = global::Ts.TsPath.TsPath.NormalizeSlashes(mapOptions.SourceRoot);
        if (sourceRoot.Length > 0)
        {
            sourceRoot = global::Ts.TsPath.TsPath.EnsureTrailingDirectorySeparator(sourceRoot);
        }
        return sourceRoot;
    }

    // emitter.go:374-398
    private string GetSourceMapDirectory(CompilerOptions mapOptions, string filePath, SourceFile? sourceFile)
    {
        if (mapOptions.SourceRoot.Length > 0)
        {
            return host.CommonSourceDirectory();
        }
        if (mapOptions.MapRoot.Length > 0)
        {
            var sourceMapDir = global::Ts.TsPath.TsPath.NormalizeSlashes(mapOptions.MapRoot);
            if (sourceFile is not null)
            {
                // For modules or multiple emit files the mapRoot will have directory structure like the sources
                // So if src\a.ts and src\lib\b.ts are compiled together user would be moving the maps into mapRoot\a.js.map and mapRoot\lib\b.js.map
                sourceMapDir = global::Ts.TsPath.TsPath.GetDirectoryPath(global::Ts.OutputPaths.OutputPaths.GetSourceFilePathInNewDir(
                    sourceFile.FileName(),
                    sourceMapDir,
                    host.GetCurrentDirectory(),
                    host.CommonSourceDirectory(),
                    host.UseCaseSensitiveFileNames()));
            }
            if (global::Ts.TsPath.TsPath.GetRootLength(sourceMapDir) == 0)
            {
                // The relative paths are relative to the common directory
                sourceMapDir = global::Ts.TsPath.TsPath.CombinePaths(host.CommonSourceDirectory(), sourceMapDir);
            }
            return sourceMapDir;
        }
        return global::Ts.TsPath.TsPath.GetDirectoryPath(global::Ts.TsPath.TsPath.NormalizePath(filePath));
    }

    // emitter.go:400-439
    private string GetSourceMappingURL(CompilerOptions mapOptions, global::Ts.SourceMap.Generator sourceMapGenerator, string filePath, string sourceMapFilePath, SourceFile? sourceFile)
    {
        if (mapOptions.InlineSourceMap.IsTrue())
        {
            // Encode the sourceMap into the sourceMap url
            return sourceMapGenerator.Base64DataURL();
        }

        var sourceMapFile = global::Ts.TsPath.TsPath.GetBaseFileName(global::Ts.TsPath.TsPath.NormalizeSlashes(sourceMapFilePath));
        if (mapOptions.MapRoot.Length > 0)
        {
            var sourceMapDir = global::Ts.TsPath.TsPath.NormalizeSlashes(mapOptions.MapRoot);
            if (sourceFile is not null)
            {
                // For modules or multiple emit files the mapRoot will have directory structure like the sources
                // So if src\a.ts and src\lib\b.ts are compiled together user would be moving the maps into mapRoot\a.js.map and mapRoot\lib\b.js.map
                sourceMapDir = global::Ts.TsPath.TsPath.GetDirectoryPath(global::Ts.OutputPaths.OutputPaths.GetSourceFilePathInNewDir(
                    sourceFile.FileName(),
                    sourceMapDir,
                    host.GetCurrentDirectory(),
                    host.CommonSourceDirectory(),
                    host.UseCaseSensitiveFileNames()));
            }
            if (global::Ts.TsPath.TsPath.GetRootLength(sourceMapDir) == 0)
            {
                // The relative paths are relative to the common directory
                sourceMapDir = global::Ts.TsPath.TsPath.CombinePaths(host.CommonSourceDirectory(), sourceMapDir);
                return global::Ts.StringUtil.StringUtil.EncodeURI(
                    global::Ts.TsPath.TsPath.GetRelativePathToDirectoryOrUrl(
                        global::Ts.TsPath.TsPath.GetDirectoryPath(global::Ts.TsPath.TsPath.NormalizePath(filePath)), // get the relative sourceMapDir path based on jsFilePath
                        global::Ts.TsPath.TsPath.CombinePaths(sourceMapDir, sourceMapFile),                          // this is where user expects to see sourceMap
                        /*isAbsolutePathAnUrl*/ true,
                        new global::Ts.TsPath.ComparePathsOptions
                        {
                            UseCaseSensitiveFileNames = host.UseCaseSensitiveFileNames(),
                            CurrentDirectory = host.GetCurrentDirectory(),
                        }));
            }
            else
            {
                return global::Ts.StringUtil.StringUtil.EncodeURI(global::Ts.TsPath.TsPath.CombinePaths(sourceMapDir, sourceMapFile));
            }
        }
        return global::Ts.StringUtil.StringUtil.EncodeURI(sourceMapFile);
    }

    // append helpers — Go `append(slice, x)` on the nil-tolerant result slices (§6.3).
    private static SourceMapEmitResult[] AppendSourceMap(SourceMapEmitResult[]? existing, SourceMapEmitResult item)
    {
        if (existing is null)
        {
            return [item];
        }
        var next = new SourceMapEmitResult[existing.Length + 1];
        Array.Copy(existing, next, existing.Length);
        next[existing.Length] = item;
        return next;
    }

    private static string[] AppendString(string[]? existing, string item)
    {
        if (existing is null)
        {
            return [item];
        }
        var next = new string[existing.Length + 1];
        Array.Copy(existing, next, existing.Length);
        next[existing.Length] = item;
        return next;
    }
}
