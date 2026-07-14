// Ported from tsgo internal/compiler/fileInclude.go.
//
// The loader populates FileIncludeReason / referencedFileData / automaticTypeDirectiveFileData
// as plain data during file loading; those data shapes and the O(1) accessors are ported real.
// The diagnostic *computation* methods (toDiagnostic, computeDiagnostic, getReferencedLocation,
// toRelatedInfo, referenceFileLocation.text/diagnosticAt) — the "explain why a file is in the
// program" surface — are ported real in the include-processor diagnostics wave.
//
// §1.5 (namespace shadowing): cross-package class names equal to a sibling namespace segment
// are fully qualified with global::.

using System;
using Ts.Ast;

namespace Ts.Compiler;

// fileInclude.go:15-27
internal enum FileIncludeKind
{
    // References from file
    Import = 0,
    ReferenceFile,
    TypeReferenceDirective,
    LibReferenceDirective,

    RootFile,
    LibFile,
    AutomaticTypeDirectiveFile,
}

// fileInclude.go:29-40
public sealed class FileIncludeReason
{
    internal FileIncludeKind Kind;
    internal object? Data;

    // Uses relative file name (lazily computed diagnostic)
    internal Diagnostic? RelativeFileNameDiag;
    internal readonly Once RelativeFileNameDiagOnce = new();

    // Uses file name as is
    internal Diagnostic? Diag;
    internal readonly Once DiagOnce = new();

    // fileInclude.go:81-83
    internal int AsIndex() => (int)Data!;

    // fileInclude.go:85-88
    internal bool AsLibFileIndex(out int index)
    {
        if (Data is int i)
        {
            index = i;
            return true;
        }
        index = 0;
        return false;
    }

    // fileInclude.go:94-96
    internal ReferencedFileData AsReferencedFileData() => (ReferencedFileData)Data!;

    // fileInclude.go:98-100
    internal AutomaticTypeDirectiveFileData AsAutomaticTypeDirectiveFileData() => (AutomaticTypeDirectiveFileData)Data!;

    // fileInclude.go:102-151
    internal ReferenceFileLocation GetReferencedLocation(Program program)
    {
        var refData = AsReferencedFileData();
        var file = program.GetSourceFileByPath(refData.File)!;
        switch (Kind)
        {
            case FileIncludeKind.Import:
            {
                Node? specifier = null;
                bool isSynthetic = false;
                if (refData.Synthetic is not null)
                {
                    specifier = refData.Synthetic;
                    isSynthetic = true;
                }
                else if (refData.Index < (file.Imports()?.Length ?? 0))
                {
                    specifier = file.Imports()![refData.Index];
                }
                else
                {
                    int augIndex = file.Imports()?.Length ?? 0;
                    foreach (var imp in file.ModuleAugmentations ?? [])
                    {
                        if (imp.Kind == SyntaxKind.StringLiteral)
                        {
                            if (augIndex == refData.Index)
                            {
                                specifier = imp;
                                break;
                            }
                            augIndex++;
                        }
                    }
                }
                // PORT-NOTE: Go's ast.SourceFile satisfies ast.HasFileName structurally; wrap for C#
                // (mirrors FileLoader.AsHasFileName).
                var resolution = program.GetResolvedModuleFromModuleSpecifier(new CompilerHasFileName(file.FileName(), file.Path()), specifier);
                return new ReferenceFileLocation
                {
                    File = file,
                    Node = specifier,
                    PackageId = resolution!.PackageId,
                    IsSynthetic = isSynthetic,
                };
            }
            case FileIncludeKind.ReferenceFile:
                return new ReferenceFileLocation
                {
                    File = file,
                    Ref = file.ReferencedFiles![refData.Index],
                };
            case FileIncludeKind.TypeReferenceDirective:
                return new ReferenceFileLocation
                {
                    File = file,
                    Ref = file.TypeReferenceDirectives![refData.Index],
                };
            case FileIncludeKind.LibReferenceDirective:
                return new ReferenceFileLocation
                {
                    File = file,
                    Ref = file.LibReferenceDirectives![refData.Index],
                };
            default:
                throw new InvalidOperationException($"unknown reason: {Kind}");
        }
    }

    // fileInclude.go:153-167
    internal Diagnostic ToDiagnostic(Program program, bool relativeFileName)
    {
        if (relativeFileName)
        {
            RelativeFileNameDiagOnce.Do(() =>
            {
                RelativeFileNameDiag = ComputeDiagnostic(program, fileName =>
                    global::Ts.TsPath.TsPath.GetRelativePathFromDirectory(program.GetCurrentDirectory(), fileName, program.ComparePathsOptions));
            });
            return RelativeFileNameDiag!;
        }
        DiagOnce.Do(() =>
        {
            Diag = ComputeDiagnostic(program, fileName => fileName);
        });
        return Diag!;
    }

    // fileInclude.go:169-215
    private Diagnostic ComputeDiagnostic(Program program, Func<string, string> toFileName)
    {
        if (this.IsReferencedFile())
        {
            return ComputeReferenceFileDiagnostic(program, toFileName);
        }
        switch (Kind)
        {
            case FileIncludeKind.RootFile:
                if (program.Opts.Config.ConfigFile is not null)
                {
                    var config = program.Opts.Config;
                    var fileName = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(config.FileNames()![AsIndex()], program.GetCurrentDirectory());
                    var matchedFileSpec = config.GetMatchedFileSpec(fileName);
                    if (matchedFileSpec != "")
                    {
                        return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Part_of_files_list_in_tsconfig_json, matchedFileSpec, toFileName(fileName));
                    }
                    var (matchedIncludeSpec, isDefaultIncludeSpec) = config.GetMatchedIncludeSpec(fileName);
                    if (matchedIncludeSpec != "")
                    {
                        if (isDefaultIncludeSpec)
                        {
                            return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Matched_by_default_include_pattern_Asterisk_Asterisk_Slash_Asterisk);
                        }
                        return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Matched_by_include_pattern_0_in_1, matchedIncludeSpec, toFileName(config.ConfigName()));
                    }
                    return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Root_file_specified_for_compilation);
                }
                return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Root_file_specified_for_compilation);
            case FileIncludeKind.AutomaticTypeDirectiveFile:
            {
                var data = AsAutomaticTypeDirectiveFileData();
                // PORT-NOTE: Go PackageId zero-value Name is ""; C# default(PackageId).Name is null — IsNullOrEmpty covers both.
                if (!program.Options().UsesWildcardTypes())
                {
                    if (!string.IsNullOrEmpty(data.PackageId.Name))
                    {
                        return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Entry_point_of_type_library_0_specified_in_compilerOptions_with_packageId_1, data.TypeReference, data.PackageId.ToString());
                    }
                    return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Entry_point_of_type_library_0_specified_in_compilerOptions, data.TypeReference);
                }
                if (!string.IsNullOrEmpty(data.PackageId.Name))
                {
                    return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Entry_point_for_implicit_type_library_0_with_packageId_1, data.TypeReference, data.PackageId.ToString());
                }
                return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Entry_point_for_implicit_type_library_0, data.TypeReference);
            }
            case FileIncludeKind.LibFile:
            {
                if (AsLibFileIndex(out var index))
                {
                    return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Library_0_specified_in_compilerOptions, program.Options().Lib![index]);
                }
                var target = ScriptTargetToString(program.Options().GetEmitScriptTarget());
                if (target != "")
                {
                    return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Default_library_for_target_0, target);
                }
                return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Default_library);
            }
            default:
                throw new InvalidOperationException($"unknown reason: {Kind}");
        }
    }

    // fileInclude.go:217-257
    private Diagnostic ComputeReferenceFileDiagnostic(Program program, Func<string, string> toFileName)
    {
        var referenceLocation = program.IncludeProcessorInstance.GetReferenceLocation(this, program);
        var referenceText = referenceLocation.Text();
        switch (Kind)
        {
            case FileIncludeKind.Import:
                if (!referenceLocation.IsSynthetic)
                {
                    if (!string.IsNullOrEmpty(referenceLocation.PackageId.Name))
                    {
                        return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Imported_via_0_from_file_1_with_packageId_2, referenceText, toFileName(referenceLocation.File.FileName()), referenceLocation.PackageId.ToString());
                    }
                    return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Imported_via_0_from_file_1, referenceText, toFileName(referenceLocation.File.FileName()));
                }
                else if (program.GetImportHelpersImportSpecifier(referenceLocation.File.Path()) == referenceLocation.Node && referenceLocation.Node is not null)
                {
                    if (!string.IsNullOrEmpty(referenceLocation.PackageId.Name))
                    {
                        return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Imported_via_0_from_file_1_with_packageId_2_to_import_importHelpers_as_specified_in_compilerOptions, referenceText, toFileName(referenceLocation.File.FileName()), referenceLocation.PackageId.ToString());
                    }
                    return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Imported_via_0_from_file_1_to_import_importHelpers_as_specified_in_compilerOptions, referenceText, toFileName(referenceLocation.File.FileName()));
                }
                else
                {
                    if (!string.IsNullOrEmpty(referenceLocation.PackageId.Name))
                    {
                        return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Imported_via_0_from_file_1_with_packageId_2_to_import_jsx_and_jsxs_factory_functions, referenceText, toFileName(referenceLocation.File.FileName()), referenceLocation.PackageId.ToString());
                    }
                    return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Imported_via_0_from_file_1_to_import_jsx_and_jsxs_factory_functions, referenceText, toFileName(referenceLocation.File.FileName()));
                }
            case FileIncludeKind.ReferenceFile:
                return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Referenced_via_0_from_file_1, referenceText, toFileName(referenceLocation.File.FileName()));
            case FileIncludeKind.TypeReferenceDirective:
                if (!string.IsNullOrEmpty(referenceLocation.PackageId.Name))
                {
                    return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Type_library_referenced_via_0_from_file_1_with_packageId_2, referenceText, toFileName(referenceLocation.File.FileName()), referenceLocation.PackageId.ToString());
                }
                return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Type_library_referenced_via_0_from_file_1, referenceText, toFileName(referenceLocation.File.FileName()));
            case FileIncludeKind.LibReferenceDirective:
                return Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Library_referenced_via_0_from_file_1, referenceText, toFileName(referenceLocation.File.FileName()));
            default:
                throw new InvalidOperationException($"unknown reason: {Kind}");
        }
    }

    // fileInclude.go:259-300
    internal Diagnostic? ToRelatedInfo(Program program)
    {
        if (this.IsReferencedFile())
        {
            return ComputeReferenceFileRelatedInfo(program);
        }
        if (program.Opts.Config.ConfigFile is null)
        {
            return null;
        }
        var config = program.Opts.Config;
        switch (Kind)
        {
            case FileIncludeKind.RootFile:
            {
                var fileName = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(config.FileNames()![AsIndex()], program.GetCurrentDirectory());
                var matchedFileSpec = config.GetMatchedFileSpec(fileName);
                if (matchedFileSpec != "")
                {
                    var filesNode = global::Ts.TsOptions.TsConfigParsing.GetTsConfigPropArrayElementValue(config.ConfigFile.SourceFile, "files", matchedFileSpec);
                    if (filesNode is not null)
                    {
                        return global::Ts.TsOptions.Errors.CreateDiagnosticForNodeInSourceFile(config.ConfigFile.SourceFile, filesNode, global::Ts.Diagnostics.Diagnostics.File_is_matched_by_files_list_specified_here);
                    }
                }
                else
                {
                    var (matchedIncludeSpec, isDefaultIncludeSpec) = config.GetMatchedIncludeSpec(fileName);
                    if (matchedIncludeSpec != "" && !isDefaultIncludeSpec)
                    {
                        var includeNode = global::Ts.TsOptions.TsConfigParsing.GetTsConfigPropArrayElementValue(config.ConfigFile.SourceFile, "include", matchedIncludeSpec);
                        if (includeNode is not null)
                        {
                            return global::Ts.TsOptions.Errors.CreateDiagnosticForNodeInSourceFile(config.ConfigFile.SourceFile, includeNode, global::Ts.Diagnostics.Diagnostics.File_is_matched_by_include_pattern_specified_here);
                        }
                    }
                }
                break;
            }
            case FileIncludeKind.AutomaticTypeDirectiveFile:
                if (!program.Options().UsesWildcardTypes())
                {
                    var data = AsAutomaticTypeDirectiveFileData();
                    var typesSyntax = GetOptionsSyntaxByArrayElementValue(program.IncludeProcessorInstance.GetCompilerOptionsObjectLiteralSyntax(program), "types", data.TypeReference);
                    if (typesSyntax is not null)
                    {
                        return global::Ts.TsOptions.Errors.CreateDiagnosticForNodeInSourceFile(config.ConfigFile.SourceFile, typesSyntax, global::Ts.Diagnostics.Diagnostics.File_is_entry_point_of_type_library_specified_here);
                    }
                }
                break;
            case FileIncludeKind.LibFile:
            {
                if (AsLibFileIndex(out var index))
                {
                    var libSyntax = GetOptionsSyntaxByArrayElementValue(program.IncludeProcessorInstance.GetCompilerOptionsObjectLiteralSyntax(program), "lib", program.Options().Lib![index]);
                    if (libSyntax is not null)
                    {
                        return global::Ts.TsOptions.Errors.CreateDiagnosticForNodeInSourceFile(config.ConfigFile.SourceFile, libSyntax, global::Ts.Diagnostics.Diagnostics.File_is_library_specified_here);
                    }
                }
                else
                {
                    var target = ScriptTargetToString(program.Options().GetEmitScriptTarget());
                    if (target != "")
                    {
                        var targetValueSyntax = global::Ts.TsOptions.TsConfigParsing.ForEachPropertyAssignment(
                            program.IncludeProcessorInstance.GetCompilerOptionsObjectLiteralSyntax(program), "target",
                            global::Ts.TsOptions.TsConfigParsing.GetCallbackForFindingPropertyAssignmentByValue(target));
                        if (targetValueSyntax is not null)
                        {
                            return global::Ts.TsOptions.Errors.CreateDiagnosticForNodeInSourceFile(config.ConfigFile.SourceFile, targetValueSyntax, global::Ts.Diagnostics.Diagnostics.File_is_default_library_for_target_specified_here);
                        }
                    }
                }
                break;
            }
            default:
                throw new InvalidOperationException($"unknown reason: {Kind}");
        }
        return null;
    }

    // fileInclude.go:302-321
    private Diagnostic? ComputeReferenceFileRelatedInfo(Program program)
    {
        var referenceLocation = program.IncludeProcessorInstance.GetReferenceLocation(this, program);
        if (referenceLocation.IsSynthetic)
        {
            return null;
        }
        switch (Kind)
        {
            case FileIncludeKind.Import:
                return referenceLocation.DiagnosticAt(global::Ts.Diagnostics.Diagnostics.File_is_included_via_import_here);
            case FileIncludeKind.ReferenceFile:
                return referenceLocation.DiagnosticAt(global::Ts.Diagnostics.Diagnostics.File_is_included_via_reference_here);
            case FileIncludeKind.TypeReferenceDirective:
                return referenceLocation.DiagnosticAt(global::Ts.Diagnostics.Diagnostics.File_is_included_via_type_library_reference_here);
            case FileIncludeKind.LibReferenceDirective:
                return referenceLocation.DiagnosticAt(global::Ts.Diagnostics.Diagnostics.File_is_included_via_library_reference_here);
            default:
                throw new InvalidOperationException($"unknown reason: {Kind}");
        }
    }

    // tsconfigparsing.go:1536-1538 — GetOptionsSyntaxByArrayElementValue has no C# port in TsOptions yet;
    // hand-inlined here (it is a two-helper composition already available as internals).
    private static Node? GetOptionsSyntaxByArrayElementValue(ObjectLiteralExpression? objectLiteral, string propKey, string elementValue) =>
        global::Ts.TsOptions.TsConfigParsing.ForEachPropertyAssignment(objectLiteral, propKey, global::Ts.TsOptions.TsConfigParsing.GetCallbackForFindingPropertyAssignmentByValue(elementValue));

    // PORT-NOTE: Go uses the generated core.ScriptTarget stringer (codegen family 6, deferred in C#).
    // C# Enum.ToString on ScriptTarget is ambiguous for aliased values (Latest == ESNext), so the
    // stringer names are hand-inlined here to keep byte-identical diagnostic text.
    private static string ScriptTargetToString(global::Ts.Core.ScriptTarget target) => target switch
    {
        global::Ts.Core.ScriptTarget.None => "None",
        global::Ts.Core.ScriptTarget.ES5 => "ES5",
        global::Ts.Core.ScriptTarget.ES2015 => "ES2015",
        global::Ts.Core.ScriptTarget.ES2016 => "ES2016",
        global::Ts.Core.ScriptTarget.ES2017 => "ES2017",
        global::Ts.Core.ScriptTarget.ES2018 => "ES2018",
        global::Ts.Core.ScriptTarget.ES2019 => "ES2019",
        global::Ts.Core.ScriptTarget.ES2020 => "ES2020",
        global::Ts.Core.ScriptTarget.ES2021 => "ES2021",
        global::Ts.Core.ScriptTarget.ES2022 => "ES2022",
        global::Ts.Core.ScriptTarget.ES2023 => "ES2023",
        global::Ts.Core.ScriptTarget.ES2024 => "ES2024",
        global::Ts.Core.ScriptTarget.ES2025 => "ES2025",
        global::Ts.Core.ScriptTarget.ESNext => "ESNext",
        global::Ts.Core.ScriptTarget.JSON => "JSON",
        _ => "ScriptTarget(" + ((int)target).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")",
    };
}

// fileInclude.go:90-92 — Go nil-receiver method → C# extension (AGENTS §4.6).
internal static class FileIncludeReasonExtensions
{
    internal static bool IsReferencedFile(this FileIncludeReason? r) =>
        r is not null && r.Kind <= FileIncludeKind.LibReferenceDirective;
}

// fileInclude.go:42-46
internal sealed class ReferencedFileData
{
    internal global::Ts.TsPath.Path File;
    internal int Index;
    internal Node? Synthetic;
}

// fileInclude.go:48-54
internal sealed class ReferenceFileLocation
{
    internal SourceFile File = null!;
    internal Node? Node;
    internal FileReference? Ref;
    internal global::Ts.Module.PackageId PackageId;
    internal bool IsSynthetic;

    // fileInclude.go:56-66
    internal string Text()
    {
        if (Node is not null)
        {
            if (!AstUtilities.NodeIsSynthesized(Node))
            {
                return File.Text()[global::Ts.Scanner.Scanner.SkipTrivia(File.Text(), Node.Loc.Pos)..Node.End];
            }
            return "\"" + Node.Text() + "\"";
        }
        return File.Text()[Ref!.Value.Pos..Ref.Value.End];
    }

    // fileInclude.go:68-74
    internal Diagnostic DiagnosticAt(global::Ts.Diagnostics.Message message, params object?[] args)
    {
        if (Node is not null)
        {
            return global::Ts.TsOptions.Errors.CreateDiagnosticForNodeInSourceFile(File, Node, message, args);
        }
        return Diagnostic.NewDiagnostic(File, Ref!.Value.TextRange, message, args);
    }
}

// fileInclude.go:76-79
internal sealed class AutomaticTypeDirectiveFileData
{
    internal string TypeReference = "";
    internal global::Ts.Module.PackageId PackageId;

    internal AutomaticTypeDirectiveFileData()
    {
    }

    internal AutomaticTypeDirectiveFileData(string typeReference, global::Ts.Module.PackageId packageId)
    {
        TypeReference = typeReference;
        PackageId = packageId;
    }
}
