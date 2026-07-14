// Ported from tsgo internal/compiler/includeprocessor.go.
//
// The include processor accumulates file-include reasons and processing diagnostics during
// loading (fileIncludeReasons map + processingDiagnostics list + the add* mutators), and
// materializes them into user-facing diagnostics on demand (getDiagnostics and the cached
// reference-location / related-info / redirect-format explainers).
//
// PORT-NOTE: updateFileIncludeProcessor (includeprocessor.go:28-33) belongs to the incremental
// program-update surface (UpdateProgram), which is not ported yet.
//
// §1.5 (namespace shadowing): cross-package class names equal to a sibling namespace segment
// are fully qualified with global::.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;

namespace Ts.Compiler;

// includeprocessor.go:15-26
internal sealed class IncludeProcessor
{
    internal Dictionary<global::Ts.TsPath.Path, List<FileIncludeReason>> FileIncludeReasons = new();
    internal List<ProcessingDiagnostic> ProcessingDiagnostics = new();

    private readonly SyncMap<FileIncludeReason, ReferenceFileLocation> reasonToReferenceLocation = new();
    private readonly SyncMap<FileIncludeReason, Diagnostic?> includeReasonToRelatedInfo = new();
    private readonly SyncMap<global::Ts.TsPath.Path, Diagnostic[]?> redirectAndFileFormat = new();
    private DiagnosticsCollection? computedDiagnostics;
    private readonly Once computedDiagnosticsOnce = new();
    private ObjectLiteralExpression? compilerOptionsSyntax;
    private readonly Once compilerOptionsSyntaxOnce = new();

    // includeprocessor.go:35-57
    internal DiagnosticsCollection GetDiagnostics(Program p)
    {
        computedDiagnosticsOnce.Do(() =>
        {
            computedDiagnostics = new DiagnosticsCollection();
            foreach (var d in ProcessingDiagnostics)
            {
                computedDiagnostics.Add(d.ToDiagnostic(p));
            }
            // map-iter: 序无关 —— DiagnosticsCollection 内部按位置排序，聚合结果与迭代序无关。
            foreach (var resolutions in p.ResolvedModulesMap.Values)
            {
                foreach (var resolvedModule in resolutions.Values)
                {
                    foreach (var diag in resolvedModule.ResolutionDiagnostics ?? [])
                    {
                        computedDiagnostics.Add(diag);
                    }
                }
            }
            // map-iter: 序无关 —— 同上。
            foreach (var typeResolutions in p.TypeResolutionsInFileMap.Values)
            {
                foreach (var resolvedTypeRef in typeResolutions.Values)
                {
                    foreach (var diag in resolvedTypeRef.ResolutionDiagnostics ?? [])
                    {
                        computedDiagnostics.Add(diag);
                    }
                }
            }
        });
        return computedDiagnostics!;
    }

    // includeprocessor.go:59-61
    internal void AddProcessingDiagnostic(params ProcessingDiagnostic[] d)
    {
        ProcessingDiagnostics.AddRange(d);
    }

    // includeprocessor.go:63-87
    internal void AddProcessingDiagnosticsForFileCasing(global::Ts.TsPath.Path file, string existingCasing, string currentCasing, FileIncludeReason reason)
    {
        bool anyReferenced = false;
        if (FileIncludeReasons.TryGetValue(file, out var reasons))
        {
            foreach (var r in reasons)
            {
                if (r.IsReferencedFile())
                {
                    anyReferenced = true;
                    break;
                }
            }
        }
        if (!reason.IsReferencedFile() && anyReferenced)
        {
            AddProcessingDiagnostic(new ProcessingDiagnostic
            {
                Kind = ProcessingDiagnosticKind.ExplainingFileInclude,
                Data = new IncludeExplainingDiagnostic
                {
                    File = file,
                    DiagnosticReason = reason,
                    Message = global::Ts.Diagnostics.Diagnostics.Already_included_file_name_0_differs_from_file_name_1_only_in_casing,
                    Args = [existingCasing, currentCasing],
                },
            });
        }
        else
        {
            AddProcessingDiagnostic(new ProcessingDiagnostic
            {
                Kind = ProcessingDiagnosticKind.ExplainingFileInclude,
                Data = new IncludeExplainingDiagnostic
                {
                    File = file,
                    DiagnosticReason = reason,
                    Message = global::Ts.Diagnostics.Diagnostics.File_name_0_differs_from_already_included_file_name_1_only_in_casing,
                    Args = [currentCasing, existingCasing],
                },
            });
        }
    }

    // includeprocessor.go:89-96
    internal ReferenceFileLocation GetReferenceLocation(FileIncludeReason r, Program program)
    {
        if (reasonToReferenceLocation.TryLoad(r, out var existing))
        {
            return existing;
        }
        var (loc, _) = reasonToReferenceLocation.LoadOrStore(r, r.GetReferencedLocation(program));
        return loc;
    }

    // includeprocessor.go:98-112
    internal ObjectLiteralExpression? GetCompilerOptionsObjectLiteralSyntax(Program program)
    {
        compilerOptionsSyntaxOnce.Do(() =>
        {
            var configFile = program.Opts.Config.ConfigFile;
            if (configFile is not null)
            {
                var compilerOptionsProperty = global::Ts.TsOptions.TsConfigParsing.ForEachTsConfigPropArray<Node>(configFile.SourceFile, "compilerOptions", node => node);
                if (compilerOptionsProperty is not null &&
                    compilerOptionsProperty.AsPropertyAssignment().Initializer is { } initializer &&
                    NodeTests.IsObjectLiteralExpression(initializer))
                {
                    compilerOptionsSyntax = initializer.AsObjectLiteralExpression();
                }
            }
            else
            {
                compilerOptionsSyntax = null;
            }
        });
        return compilerOptionsSyntax;
    }

    // includeprocessor.go:114-121
    internal Diagnostic? GetRelatedInfo(FileIncludeReason r, Program program)
    {
        if (includeReasonToRelatedInfo.TryLoad(r, out var existing))
        {
            return existing;
        }
        var (relatedInfo, _) = includeReasonToRelatedInfo.LoadOrStore(r, r.ToRelatedInfo(program));
        return relatedInfo;
    }

    // includeprocessor.go:123-184
    internal Diagnostic[]? ExplainRedirectAndImpliedFormat(Program program, global::Ts.TsPath.Path filePath, Func<string, string> toFileName)
    {
        if (redirectAndFileFormat.TryLoad(filePath, out var existing))
        {
            return existing;
        }
        HasFileName file;
        SourceFile? sourceFile = null;
        var redirectsFile = program.RedirectFilesByPathMap?.GetValueOrDefault(filePath);
        if (redirectsFile is not null)
        {
            file = redirectsFile;
        }
        else
        {
            sourceFile = program.GetSourceFileByPath(filePath);
            if (sourceFile is null)
            {
                return null;
            }
            // PORT-NOTE: Go's ast.SourceFile satisfies ast.HasFileName structurally; the C# SourceFile
            // declares FileName()/Path() without `: HasFileName`, so wrap (mirrors FileLoader.AsHasFileName).
            file = new CompilerHasFileName(sourceFile.FileName(), sourceFile.Path());
        }
        List<Diagnostic>? result = null;
        var source = program.GetSourceOfProjectReferenceIfOutputIncluded(file);
        if (source != file.FileName())
        {
            (result ??= []).Add(Diagnostic.NewCompilerDiagnostic(
                global::Ts.Diagnostics.Diagnostics.File_is_output_of_project_reference_source_0,
                toFileName(source)));
        }

        if (redirectsFile is not null)
        {
            var targetFile = program.GetSourceFileByPath(redirectsFile.Target);
            (result ??= []).Add(Diagnostic.NewCompilerDiagnostic(
                global::Ts.Diagnostics.Diagnostics.File_redirects_to_file_0,
                toFileName(targetFile!.FileName())));
        }

        if (sourceFile is not null && AstUtilities.IsExternalOrCommonJSModule(sourceFile))
        {
            var metaData = program.GetSourceFileMetaData(file.Path());
            switch (program.GetImpliedNodeFormatForEmit(file))
            {
                case ModuleKind.ESNext:
                    if (metaData.PackageJsonType == "module")
                    {
                        (result ??= []).Add(Diagnostic.NewCompilerDiagnostic(
                            global::Ts.Diagnostics.Diagnostics.File_is_ECMAScript_module_because_0_has_field_type_with_value_module,
                            toFileName(metaData.PackageJsonDirectory + "/package.json")));
                    }
                    break;
                case ModuleKind.CommonJS:
                    if (!string.IsNullOrEmpty(metaData.PackageJsonType))
                    {
                        (result ??= []).Add(Diagnostic.NewCompilerDiagnostic(
                            global::Ts.Diagnostics.Diagnostics.File_is_CommonJS_module_because_0_has_field_type_whose_value_is_not_module,
                            toFileName(metaData.PackageJsonDirectory + "/package.json")));
                    }
                    else if (!string.IsNullOrEmpty(metaData.PackageJsonDirectory))
                    {
                        if (string.IsNullOrEmpty(metaData.PackageJsonType))
                        {
                            (result ??= []).Add(Diagnostic.NewCompilerDiagnostic(
                                global::Ts.Diagnostics.Diagnostics.File_is_CommonJS_module_because_0_does_not_have_field_type,
                                toFileName(metaData.PackageJsonDirectory + "/package.json")));
                        }
                    }
                    else
                    {
                        (result ??= []).Add(Diagnostic.NewCompilerDiagnostic(
                            global::Ts.Diagnostics.Diagnostics.File_is_CommonJS_module_because_package_json_was_not_found));
                    }
                    break;
            }
        }

        var (stored, _) = redirectAndFileFormat.LoadOrStore(filePath, result?.ToArray());
        return stored;
    }
}
