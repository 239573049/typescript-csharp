// Ported from tsgo internal/compiler/fileloader.go.
//
// This drives the real load pipeline: build root parse tasks, create the module.Resolver, run the
// parallel filesParser, and assemble the processedFiles snapshot. Import resolution flows through
// module.Resolver; binding is driven later by Program.BindSourceFiles.
//
// PORT-NOTE (phase 4/5): deferred sub-surfaces are marked inline: the type-only / import-syntax
// resolution-mode overrides depend on ast helpers not yet ported (IsExclusivelyTypeOnlyImportOrExport,
// GetResolutionModeOverride, IsImportCall, ShouldTransformImportCall). They throw where reached; the
// phase 4/5 milestone (default module resolution) never reaches them.
//
// §1.5 (namespace shadowing): sibling-namespace static classes are fully qualified with global::.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.Module;
using Ts.PackageJson;

namespace Ts.Compiler;

// fileloader.go:21-25
internal sealed class LibResolution
{
    internal string LibraryName = "";
    internal ResolvedModule? Resolution;
    internal List<DiagAndArgs>? Trace;
}

// fileloader.go:27-31 (Go: LibFile.path is a string, not a tspath.Path)
public sealed class LibFile
{
    public string Name = "";
    internal string PathValue = "";
    public bool Replaced;

    internal LibFile()
    {
    }

    internal LibFile(string name, string path, bool replaced)
    {
        Name = name;
        PathValue = path;
        Replaced = replaced;
    }
}

// fileloader.go:33-36
internal sealed class SourceFileFromReferenceDiagnostic
{
    internal global::Ts.Diagnostics.Message Message = null!;
    internal object?[]? Args;
}

// fileloader.go:62-68
internal sealed class RedirectsFile : HasFileName
{
    // Index of file at which this redirect file needs to be iterated
    internal int Index;
    internal string FileNameValue = "";
    internal global::Ts.TsPath.Path PathValue;
    internal global::Ts.TsPath.Path Target;

    public string FileName() => FileNameValue;

    public global::Ts.TsPath.Path Path() => PathValue;
}

// fileloader.go:70-74
public sealed class DuplicateSourceFile
{
    public SourceFileParseOptions ParseOptions;
    public UInt128 Hash;
    public ScriptKind ScriptKind;
}

// fileloader.go:117-120
internal sealed class JsxRuntimeImportSpecifier
{
    internal string ModuleReference = "";
    internal Node Specifier = null!;
}

// fileloader.go:86-115 — the immutable snapshot Program embeds. C# has no struct embedding, so
// Program composes this (see Program.cs).
internal sealed class ProcessedFiles
{
    internal Resolver Resolver = null!;
    internal SourceFile[] Files = [];
    internal DuplicateSourceFile[] DuplicateSourceFiles = [];
    internal Dictionary<global::Ts.TsPath.Path, SourceFile> FilesByPath = new();
    internal ProjectReferenceFileMapper ProjectReferenceFileMapper = null!;
    internal string[] MissingFiles = [];
    internal Dictionary<global::Ts.TsPath.Path, ModeAwareCache<ResolvedModule>> ResolvedModules = new();
    internal Dictionary<global::Ts.TsPath.Path, ModeAwareCache<ResolvedTypeReferenceDirective>> TypeResolutionsInFile = new();
    internal Dictionary<global::Ts.TsPath.Path, SourceFileMetaData> SourceFileMetaDatas = new();
    internal Dictionary<global::Ts.TsPath.Path, JsxRuntimeImportSpecifier>? JsxRuntimeImportSpecifiers;
    internal Dictionary<global::Ts.TsPath.Path, Node>? ImportHelpersImportSpecifiers;
    internal Dictionary<global::Ts.TsPath.Path, LibFile> LibFiles = new();
    internal Set<global::Ts.TsPath.Path> SourceFilesFoundSearchingNodeModules = new();
    internal IncludeProcessor IncludeProcessor = null!;
    internal Dictionary<global::Ts.TsPath.Path, string>? OutputFileToProjectReferenceSource;
    internal Dictionary<global::Ts.TsPath.Path, List<string>>? RedirectTargetsMap;
    internal Dictionary<global::Ts.TsPath.Path, RedirectsFile>? RedirectFilesByPath;
    internal bool FinishedProcessing;

    // Go embeds processedFiles in Program by value; UpdateProgram's struct copy is this shallow clone.
    internal ProcessedFiles ShallowClone() => (ProcessedFiles)MemberwiseClone();
}

// Minimal HasFileName carrier — Go uses ast.NewHasFileName(fileName, path); the C# Ast port has no
// such factory yet, so the compiler package provides one.
internal sealed class CompilerHasFileName : HasFileName
{
    private readonly string fileName;
    private readonly global::Ts.TsPath.Path path;

    internal CompilerHasFileName(string fileName, global::Ts.TsPath.Path path)
    {
        this.fileName = fileName;
        this.path = path;
    }

    public string FileName() => fileName;

    public global::Ts.TsPath.Path Path() => path;
}

// fileloader.go:38-60
internal sealed class FileLoader
{
    internal ProgramOptions Opts = null!;
    internal Resolver Resolver = null!;
    internal string DefaultLibraryPath = "";
    internal global::Ts.TsPath.ComparePathsOptions ComparePathsOptions;
    internal string[][] SupportedExtensions = [];
    internal string[][] SupportedExtensionsWithJsonIfResolveJsonModule = [];

    internal FilesParser FilesParser = null!;
    internal List<ParseTask> RootTasks = new();

    internal int TotalFileCount;
    internal int LibFileCount;

    private readonly global::System.Threading.Lock factoryMu = new();
    private readonly global::Ts.Ast.NodeFactory factory = new();

    internal ProjectReferenceFileMapper ProjectReferenceFileMapper = null!;
    internal readonly Set<global::Ts.TsPath.Path> DtsDirectories = new();

    internal readonly SyncMap<string, LibFile> PathForLibFileCache = new();
    internal readonly SyncMap<global::Ts.TsPath.Path, LibResolution> PathForLibFileResolutions = new();

    private const string ExternalHelpersModuleNameText = "tslib"; // fileloader.go:526

    // fileloader.go:122-185
    internal static ProcessedFiles ProcessAllProgramFiles(ProgramOptions opts, bool singleThreaded)
    {
        var compilerOptions = opts.Config.CompilerOptions()!;
        var rootFiles = opts.Config.FileNames() ?? [];
        var supportedExtensions = global::Ts.TsOptions.TsConfigParsing.GetSupportedExtensions(compilerOptions, null);
        var supportedExtensionsWithJsonIfResolveJsonModule = global::Ts.TsOptions.TsConfigParsing.GetSupportedExtensionsWithJsonIfResolveJsonModule(compilerOptions, supportedExtensions);
        var maxNodeModuleJsDepth = compilerOptions.MaxNodeModuleJsDepth ?? 0;
        var loader = new FileLoader
        {
            Opts = opts,
            DefaultLibraryPath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(opts.Host.DefaultLibraryPath(), opts.Host.GetCurrentDirectory()),
            ComparePathsOptions = new global::Ts.TsPath.ComparePathsOptions
            {
                UseCaseSensitiveFileNames = opts.Host.FS().UseCaseSensitiveFileNames(),
                CurrentDirectory = opts.Host.GetCurrentDirectory(),
            },
            FilesParser = new FilesParser
            {
                Wg = WorkGroup.Create(singleThreaded),
                MaxDepth = maxNodeModuleJsDepth,
            },
            RootTasks = new List<ParseTask>(rootFiles.Length + (compilerOptions.Lib?.Length ?? 0)),
            SupportedExtensions = supportedExtensions,
            SupportedExtensionsWithJsonIfResolveJsonModule = supportedExtensionsWithJsonIfResolveJsonModule,
        };
        loader.AddProjectReferenceTasks(singleThreaded);
        loader.Resolver = new Resolver(loader.ProjectReferenceFileMapper.Host!, compilerOptions, opts.TypingsLocation, opts.ProjectName);
        for (int index = 0; index < rootFiles.Length; index++)
        {
            loader.AddRootFileTask(rootFiles[index], null, new FileIncludeReason { Kind = FileIncludeKind.RootFile, Data = index });
        }
        if (rootFiles.Length > 0 && compilerOptions.NoLib.IsFalseOrUnknown())
        {
            if (compilerOptions.Lib is null)
            {
                var name = global::Ts.TsOptions.EnumMaps.GetDefaultLibFileName(compilerOptions);
                var libFile = loader.PathForLibFile(name);
                loader.AddRootTask(libFile.PathValue, libFile, new FileIncludeReason { Kind = FileIncludeKind.LibFile });
            }
            else
            {
                for (int index = 0; index < compilerOptions.Lib.Length; index++)
                {
                    if (global::Ts.TsOptions.EnumMaps.TryGetLibFileName(compilerOptions.Lib[index], out var name))
                    {
                        var libFile = loader.PathForLibFile(name);
                        loader.AddRootTask(libFile.PathValue, libFile, new FileIncludeReason { Kind = FileIncludeKind.LibFile, Data = index });
                    }
                    // !!! error on unknown name
                }
            }
        }

        if (rootFiles.Length > 0)
        {
            loader.AddAutomaticTypeDirectiveTasks();
        }

        loader.FilesParser.Parse(loader, loader.RootTasks);

        // Clear out loader and host to ensure it's not used post program creation.
        loader.ProjectReferenceFileMapper.Loader = null;
        loader.ProjectReferenceFileMapper.Host = null;

        return loader.FilesParser.GetProcessedFiles(loader);
    }

    // fileloader.go:187-189
    internal global::Ts.TsPath.Path ToPath(string file) =>
        global::Ts.TsPath.TsPath.ToPath(file, Opts.Host.GetCurrentDirectory(), Opts.Host.FS().UseCaseSensitiveFileNames());

    // fileloader.go:191-200
    private void AddRootTask(string fileName, LibFile? libFile, FileIncludeReason includeReason)
    {
        var absPath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(fileName, Opts.Host.GetCurrentDirectory());
        if (Opts.Config.CompilerOptions()!.AllowNonTsExtensions.IsTrue() || global::Ts.TsPath.TsPath.HasExtension(absPath))
        {
            RootTasks.Add(new ParseTask
            {
                NormalizedFilePath = absPath,
                LibFile = libFile,
                IncludeReason = includeReason,
            });
        }
    }

    // PORT-NOTE: Go's ast.SourceFile satisfies ast.HasFileName structurally; the C# Ast port declares
    // FileName()/Path() on SourceFile but not `: HasFileName`, so wrap it at the call sites that pass a
    // SourceFile where a HasFileName is expected. Path()/FileName() are identical, so behavior matches.
    private static HasFileName AsHasFileName(SourceFile file) => new CompilerHasFileName(file.FileName(), file.Path());

    // fileloader.go:202-227
    private void AddRootFileTask(string fileName, LibFile? libFile, FileIncludeReason includeReason)
    {
        var currDir = Opts.Host.GetCurrentDirectory();
        var absPath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(fileName, currDir);
        var containingFile = currDir;
        if (Opts.Config.ConfigFile is not null)
        {
            containingFile = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(Opts.Config.ConfigFile.SourceFile.FileName(), currDir);
        }
        var (resolvedFile, diagnostic) = GetSourceFileFromReference(absPath, fileName, containingFile, includeReason);
        var rootTask = new ParseTask
        {
            NormalizedFilePath = resolvedFile,
            LibFile = libFile,
            IncludeReason = includeReason,
        };
        if (diagnostic is not null)
        {
            rootTask.NormalizedFilePath = absPath;
            rootTask.ProcessingDiagnostics = new List<ProcessingDiagnostic>
            {
                new()
                {
                    Kind = ProcessingDiagnosticKind.ExplainingFileInclude,
                    Data = new IncludeExplainingDiagnostic
                    {
                        DiagnosticReason = includeReason,
                        Message = diagnostic.Message,
                        Args = diagnostic.Args,
                    },
                },
            };
        }
        RootTasks.Add(rootTask);
    }

    // fileloader.go:229-242
    private void AddAutomaticTypeDirectiveTasks()
    {
        string containingDirectory;
        var compilerOptions = Opts.Config.CompilerOptions()!;
        if (compilerOptions.ConfigFilePath != "")
        {
            containingDirectory = global::Ts.TsPath.TsPath.GetDirectoryPath(compilerOptions.ConfigFilePath);
        }
        else
        {
            containingDirectory = Opts.Host.GetCurrentDirectory();
        }
        var containingFileName = global::Ts.TsPath.TsPath.CombinePaths(containingDirectory, global::Ts.Module.Module.InferredTypesContainingFile);
        RootTasks.Add(new ParseTask
        {
            NormalizedFilePath = containingFileName,
            IsForAutomaticTypeDirective = true,
        });
    }

    // fileloader.go:244-295
    internal (List<ResolvedRef> ToParse, ModeAwareCache<ResolvedTypeReferenceDirective> TypeResolutionsInFile, List<DiagAndArgs> TypeResolutionsTrace, List<ProcessingDiagnostic> PDiagnostics) ResolveAutomaticTypeDirectives(string containingFileName)
    {
        var toParse = new List<ResolvedRef>();
        var typeResolutionsInFile = new ModeAwareCache<ResolvedTypeReferenceDirective>();
        var typeResolutionsTrace = new List<DiagAndArgs>();
        var pDiagnostics = new List<ProcessingDiagnostic>();

        var automaticTypeDirectiveNames = global::Ts.Module.Module.GetAutomaticTypeDirectiveNames(Opts.Config.CompilerOptions()!, Opts.Host);
        foreach (var name in automaticTypeDirectiveNames)
        {
            // Under node16/nodenext load `types`/ata include names as cjs resolution results (undefined mode).
            var resolutionMode = global::Ts.Core.Core.ResolutionModeNone;
            var (resolved, trace) = Resolver.ResolveTypeReferenceDirective(name, containingFileName, resolutionMode, null);
            typeResolutionsInFile[new ModeAwareCacheKey(name, resolutionMode)] = resolved;
            if (trace is not null)
            {
                typeResolutionsTrace.AddRange(trace);
            }
            if (resolved.IsResolved())
            {
                toParse.Add(new ResolvedRef
                {
                    FileName = resolved.ResolvedFileName,
                    IncreaseDepth = resolved.IsExternalLibraryImport,
                    ElideOnDepth = false,
                    IncludeReason = new FileIncludeReason
                    {
                        Kind = FileIncludeKind.AutomaticTypeDirectiveFile,
                        Data = new AutomaticTypeDirectiveFileData(name, resolved.PackageId),
                    },
                    PackageId = resolved.PackageId,
                });
            }
            else
            {
                pDiagnostics.Add(new ProcessingDiagnostic
                {
                    Kind = ProcessingDiagnosticKind.ExplainingFileInclude,
                    Data = new IncludeExplainingDiagnostic
                    {
                        DiagnosticReason = new FileIncludeReason
                        {
                            Kind = FileIncludeKind.AutomaticTypeDirectiveFile,
                            Data = new AutomaticTypeDirectiveFileData { TypeReference = name },
                        },
                        Message = global::Ts.Diagnostics.Diagnostics.Cannot_find_type_definition_file_for_0,
                        Args = [name],
                    },
                });
            }
        }
        return (toParse, typeResolutionsInFile, typeResolutionsTrace, pDiagnostics);
    }

    // fileloader.go:297-313
    private void AddProjectReferenceTasks(bool singleThreaded)
    {
        ProjectReferenceFileMapper = new ProjectReferenceFileMapper
        {
            Opts = Opts,
            Host = Opts.Host,
        };
        var projectReferences = Opts.Config.ResolvedProjectReferencePaths();
        if (projectReferences is null || projectReferences.Length == 0)
        {
            return;
        }

        var parser = new ProjectReferenceParser
        {
            Loader = this,
            Wg = WorkGroup.Create(singleThreaded),
        };
        var rootTasks = ProjectReferenceParseTasks.Create(projectReferences);
        parser.Parse(rootTasks);
    }

    // fileloader.go:315-319
    internal void SortLibs(List<SourceFile> libFiles)
    {
        libFiles.Sort((f1, f2) => GetDefaultLibFilePriority(f1).CompareTo(GetDefaultLibFilePriority(f2)));
    }

    // fileloader.go:321-339
    private int GetDefaultLibFilePriority(SourceFile a)
    {
        // defaultLibraryPath and a.FileName() are absolute and normalized; a prefix check suffices.
        var defaultLibraryPath = global::Ts.TsPath.TsPath.RemoveTrailingDirectorySeparator(DefaultLibraryPath);
        var aFileName = a.FileName();

        if (aFileName.StartsWith(defaultLibraryPath, StringComparison.Ordinal) && aFileName.Length > defaultLibraryPath.Length && aFileName[defaultLibraryPath.Length] == global::Ts.TsPath.TsPath.DirectorySeparator)
        {
            var basename = aFileName[(aFileName.LastIndexOf(global::Ts.TsPath.TsPath.DirectorySeparator) + 1)..];
            if (basename == "lib.d.ts" || basename == "lib.es6.d.ts")
            {
                return 0;
            }
            var name = basename;
            if (name.StartsWith("lib.", StringComparison.Ordinal))
            {
                name = name["lib.".Length..];
            }
            if (name.EndsWith(".d.ts", StringComparison.Ordinal))
            {
                name = name[..^".d.ts".Length];
            }
            var index = Array.IndexOf(global::Ts.TsOptions.EnumMaps.Libs, name);
            if (index != -1)
            {
                return index + 1;
            }
        }
        return global::Ts.TsOptions.EnumMaps.Libs.Length + 2;
    }

    // fileloader.go:341-362
    internal SourceFileMetaData LoadSourceFileMetaData(string fileName)
    {
        var packageJsonScope = Resolver.GetPackageScopeForPath(global::Ts.TsPath.TsPath.GetDirectoryPath(fileName));
        var moduleResolutionKind = Opts.Config.CompilerOptions()!.GetModuleResolutionKind();

        string packageJsonType = "";
        string packageJsonDirectory = "";
        if (packageJsonScope is not null && packageJsonScope.Exists())
        {
            packageJsonDirectory = packageJsonScope.PackageDirectory;
            if (packageJsonScope.Contents!.Type.GetValue(out var value))
            {
                if ((!global::Ts.TsPath.TsPath.FileExtensionIsOneOf(fileName, MetaDataModuleExtensions) &&
                        ModuleResolutionKind.Node16 <= moduleResolutionKind && moduleResolutionKind <= ModuleResolutionKind.NodeNext) ||
                    fileName.Contains("/node_modules/", StringComparison.Ordinal))
                {
                    packageJsonType = value!;
                }
            }
        }

        var impliedNodeFormat = ModuleFormat.GetImpliedNodeFormatForFile(fileName, packageJsonType);
        return new SourceFileMetaData
        {
            PackageJsonType = packageJsonType,
            PackageJsonDirectory = packageJsonDirectory,
            ImpliedNodeFormat = impliedNodeFormat,
        };
    }

    private static readonly string[] MetaDataModuleExtensions =
    [
        global::Ts.TsPath.TsPath.ExtensionMts,
        global::Ts.TsPath.TsPath.ExtensionCts,
        global::Ts.TsPath.TsPath.ExtensionMjs,
        global::Ts.TsPath.TsPath.ExtensionCjs,
    ];

    // fileloader.go:364-376
    internal SourceFile? ParseSourceFile(ParseTask t)
    {
        Action releaseTracing = global::Ts.Tracing.TracingExtensions.Push(
            Opts.Tracing, global::Ts.Tracing.Phase.Parse, "createSourceFile",
            new Dictionary<string, object?> { ["path"] = t.NormalizedFilePath }, true);
        try
        {
            return ParseSourceFileWorker(t);
        }
        finally
        {
            releaseTracing();
        }
    }

    private SourceFile? ParseSourceFileWorker(ParseTask t)
    {
        var path = ToPath(t.NormalizedFilePath);
        var options = ProjectReferenceFileMapper.GetCompilerOptionsForFile(t);
        var sourceFile = Opts.Host.GetSourceFile(new SourceFileParseOptions
        {
            FileName = t.NormalizedFilePath,
            Path = path,
            ExternalModuleIndicatorOptions = ModuleFormat.GetExternalModuleIndicatorOptions(t.NormalizedFilePath, options, t.Metadata),
        });
        if (sourceFile is not null)
        {
            // PORT-NOTE: populate SourceFile.Imports here (the parser's collector is a phase-4 stub).
            // See ImportCollector for the deviation rationale. No-op once the parser port lands.
            ImportCollector.CollectExternalModuleReferences(sourceFile);
        }
        return sourceFile;
    }

    // fileloader.go:378-385
    internal bool IsSupportedExtension(string canonicalFileName)
    {
        foreach (var group in SupportedExtensionsWithJsonIfResolveJsonModule)
        {
            if (global::Ts.TsPath.TsPath.FileExtensionIsOneOf(canonicalFileName, group))
            {
                return true;
            }
        }
        return false;
    }

    // fileloader.go:387-432
    private (string ResolvedFileName, SourceFileFromReferenceDiagnostic? Diagnostic) GetSourceFileFromReference(
        string fileName,
        string referenceText,
        string containingFile,
        FileIncludeReason includeReason)
    {
        var options = Opts.Config.CompilerOptions()!;
        var allowNonTsExtensions = options.AllowNonTsExtensions.IsTrue();
        var diagnosticFileName = global::Ts.TsPath.TsPath.NormalizeSlashes(referenceText);

        if (global::Ts.TsPath.TsPath.HasExtension(fileName))
        {
            var canonicalFileName = global::Ts.TsPath.TsPath.GetCanonicalFileName(fileName, Opts.Host.FS().UseCaseSensitiveFileNames());
            if (!allowNonTsExtensions && !IsSupportedExtension(canonicalFileName))
            {
                if (global::Ts.TsPath.TsPath.HasJSFileExtension(canonicalFileName))
                {
                    return ("", new SourceFileFromReferenceDiagnostic { Message = global::Ts.Diagnostics.Diagnostics.File_0_is_a_JavaScript_file_Did_you_mean_to_enable_the_allowJs_option, Args = [diagnosticFileName] });
                }
                return ("", new SourceFileFromReferenceDiagnostic { Message = global::Ts.Diagnostics.Diagnostics.File_0_has_an_unsupported_extension_The_only_supported_extensions_are_1, Args = [diagnosticFileName, "'" + string.Join("', '", global::Ts.Core.CoreUtil.Flatten(SupportedExtensions) ?? []) + "'"] });
            }

            if (!Opts.Host.FS().FileExists(fileName))
            {
                return ("", new SourceFileFromReferenceDiagnostic { Message = global::Ts.Diagnostics.Diagnostics.File_0_not_found, Args = [diagnosticFileName] });
            }

            if (includeReason.IsReferencedFile() && global::Ts.TsPath.TsPath.GetCanonicalFileName(containingFile, Opts.Host.FS().UseCaseSensitiveFileNames()) == canonicalFileName)
            {
                return ("", new SourceFileFromReferenceDiagnostic { Message = global::Ts.Diagnostics.Diagnostics.A_file_cannot_have_a_reference_to_itself });
            }
            return (fileName, null);
        }

        if (allowNonTsExtensions && Opts.Host.FS().FileExists(fileName))
        {
            return (fileName, null);
        }

        if (allowNonTsExtensions)
        {
            return ("", new SourceFileFromReferenceDiagnostic { Message = global::Ts.Diagnostics.Diagnostics.File_0_not_found, Args = [diagnosticFileName] });
        }

        foreach (var ext in SupportedExtensions[0])
        {
            var candidate = fileName + ext;
            if (Opts.Host.FS().FileExists(candidate))
            {
                return (candidate, null);
            }
        }

        return ("", new SourceFileFromReferenceDiagnostic { Message = global::Ts.Diagnostics.Diagnostics.Could_not_resolve_the_path_0_with_the_extensions_Colon_1, Args = [diagnosticFileName, "'" + string.Join("', '", global::Ts.Core.CoreUtil.Flatten(SupportedExtensions) ?? []) + "'"] });
    }

    // fileloader.go:434-471
    internal (ResolvedRef? Ref, ProcessingDiagnostic? Diagnostic) ResolveTripleslashPathReference(string moduleName, string containingFile, int index)
    {
        var basePath = global::Ts.TsPath.TsPath.GetDirectoryPath(containingFile);
        var referencedFileName = moduleName;

        if (!global::Ts.TsPath.TsPath.IsRootedDiskPath(moduleName))
        {
            referencedFileName = global::Ts.TsPath.TsPath.CombinePaths(basePath, moduleName);
        }
        var normalizedFileName = global::Ts.TsPath.TsPath.NormalizePath(referencedFileName);
        var includeReason = new FileIncludeReason
        {
            Kind = FileIncludeKind.ReferenceFile,
            Data = new ReferencedFileData
            {
                File = ToPath(containingFile),
                Index = index,
            },
        };

        var (resolvedFileName, diagnostic) = GetSourceFileFromReference(normalizedFileName, moduleName, containingFile, includeReason);
        if (diagnostic is not null)
        {
            return (null, new ProcessingDiagnostic
            {
                Kind = ProcessingDiagnosticKind.ExplainingFileInclude,
                Data = new IncludeExplainingDiagnostic
                {
                    DiagnosticReason = includeReason,
                    Message = diagnostic.Message,
                    Args = diagnostic.Args,
                },
            });
        }

        return (new ResolvedRef
        {
            FileName = resolvedFileName,
            IncludeReason = includeReason,
        }, null);
    }

    // fileloader.go:473-524
    internal void ResolveTypeReferenceDirectives(ParseTask t)
    {
        var file = t.File!;
        if (file.TypeReferenceDirectives is null || file.TypeReferenceDirectives.Length == 0)
        {
            return;
        }
        var meta = t.Metadata;

        var typeResolutionsInFile = new ModeAwareCache<ResolvedTypeReferenceDirective>();
        var typeResolutionsTrace = new List<DiagAndArgs>();
        for (int index = 0; index < file.TypeReferenceDirectives.Length; index++)
        {
            var reference = file.TypeReferenceDirectives[index];
            var (redirect, fileName) = ProjectReferenceFileMapper.GetRedirectForResolution(AsHasFileName(file));
            var resolutionMode = GetModeForTypeReferenceDirectiveInFile(reference, file, meta, ModuleFormat.CompilerOptionsForRedirect(Opts.Config.CompilerOptions()!, redirect));
            var (resolved, trace) = Resolver.ResolveTypeReferenceDirective(reference.FileName, fileName, resolutionMode, AsResolvedProjectReference(redirect));
            typeResolutionsInFile[new ModeAwareCacheKey(reference.FileName, resolutionMode)] = resolved;
            var includeReason = new FileIncludeReason
            {
                Kind = FileIncludeKind.TypeReferenceDirective,
                Data = new ReferencedFileData { File = t.PathValue, Index = index },
            };
            if (trace is not null)
            {
                typeResolutionsTrace.AddRange(trace);
            }

            if (resolved.IsResolved())
            {
                t.AddSubTask(new ResolvedRef
                {
                    FileName = resolved.ResolvedFileName,
                    IncreaseDepth = resolved.IsExternalLibraryImport,
                    ElideOnDepth = false,
                    IncludeReason = includeReason,
                    PackageId = resolved.PackageId,
                }, null);
            }
            else
            {
                t.AddProcessingDiagnostic(new ProcessingDiagnostic
                {
                    Kind = ProcessingDiagnosticKind.UnknownReference,
                    Data = includeReason,
                });
            }
        }

        t.TypeResolutionsInFile = typeResolutionsInFile;
        t.TypeResolutionsTrace = typeResolutionsTrace;
    }

    // fileloader.go:528-632
    internal void ResolveImportsAndModuleAugmentations(ParseTask t)
    {
        var file = t.File!;
        var meta = t.Metadata;

        var moduleNames = new List<Node>((file.Imports()?.Length ?? 0) + (file.ModuleAugmentations?.Length ?? 0) + 2);

        var isJavaScriptFile = AstUtilities.IsSourceFileJS(file);
        var isExternalModuleFile = AstUtilities.IsExternalModule(file);

        var (redirect, fileName) = ProjectReferenceFileMapper.GetRedirectForResolution(AsHasFileName(file));
        var optionsForFile = ModuleFormat.CompilerOptionsForRedirect(Opts.Config.CompilerOptions()!, redirect);
        if (isJavaScriptFile || (!file.IsDeclarationFile && (optionsForFile.GetIsolatedModules() || isExternalModuleFile)))
        {
            if (optionsForFile.ImportHelpers.IsTrue())
            {
                var specifier = CreateSyntheticImport(ExternalHelpersModuleNameText, file);
                moduleNames.Add(specifier);
                t.ImportHelpersImportSpecifier = specifier;
            }
        }

        if (file.ScriptKind == ScriptKind.Jsx || file.ScriptKind == ScriptKind.Tsx)
        {
            var jsxImport = GetJSXRuntimeImport(GetJSXImplicitImportBase(optionsForFile, file), optionsForFile);
            if (jsxImport != "")
            {
                var specifier = CreateSyntheticImport(jsxImport, file);
                moduleNames.Add(specifier);
                t.JsxRuntimeImportSpecifier = new JsxRuntimeImportSpecifier
                {
                    ModuleReference = jsxImport,
                    Specifier = specifier,
                };
            }
        }

        var importsStart = moduleNames.Count;

        if (file.Imports() is not null)
        {
            moduleNames.AddRange(file.Imports()!);
        }
        if (file.ModuleAugmentations is not null)
        {
            foreach (var imp in file.ModuleAugmentations)
            {
                if (imp.Kind == SyntaxKind.StringLiteral)
                {
                    moduleNames.Add(imp);
                }
                // Do nothing for an Identifier; no module resolution for `declare global`.
            }
        }

        if (moduleNames.Count != 0)
        {
            var resolutionsInFile = new ModeAwareCache<ResolvedModule>();
            var resolutionsTrace = new List<DiagAndArgs>();

            for (int index = 0; index < moduleNames.Count; index++)
            {
                var entry = moduleNames[index];
                var moduleName = entry.Text();
                if (moduleName == "")
                {
                    continue;
                }

                var mode = GetModeForUsageLocation(file.FileName(), meta, entry, optionsForFile);
                var (resolvedModule, trace) = Resolver.ResolveModuleName(moduleName, fileName, mode, AsResolvedProjectReference(redirect));
                resolutionsInFile[new ModeAwareCacheKey(moduleName, mode)] = resolvedModule;
                if (trace is not null)
                {
                    resolutionsTrace.AddRange(trace);
                }

                if (!resolvedModule.IsResolved())
                {
                    continue;
                }

                var resolvedFileName = resolvedModule.ResolvedFileName;
                var isFromNodeModulesSearch = resolvedModule.IsExternalLibraryImport;
                // Don't treat redirected files as JS files.
                var isJsFile = !global::Ts.TsPath.TsPath.FileExtensionIsOneOf(resolvedFileName, global::Ts.TsPath.TsPath.SupportedTSExtensionsWithJsonFlat) &&
                    ProjectReferenceFileMapper.GetRedirectParsedCommandLineForResolution(new CompilerHasFileName(resolvedFileName, ToPath(resolvedFileName))) is null;
                var isJsFileFromNodeModules = isFromNodeModulesSearch && isJsFile && resolvedFileName.Contains("/node_modules/", StringComparison.Ordinal);

                var importIndex = index - importsStart;

                var imports = file.Imports();
                var shouldAddFile = moduleName != "" &&
                    global::Ts.Module.Module.GetResolutionDiagnostic(optionsForFile, resolvedModule, file) is null &&
                    !optionsForFile.NoResolve.IsTrue() &&
                    !(isJsFile && !optionsForFile.GetAllowJS()) &&
                    (importIndex < 0 || (imports is not null && importIndex < imports.Length && (AstUtilities.IsInJSFile(imports[importIndex]) || (imports[importIndex].Flags & NodeFlags.JSDoc) == 0)));

                if (shouldAddFile)
                {
                    t.AddSubTask(new ResolvedRef
                    {
                        FileName = resolvedFileName,
                        IncreaseDepth = resolvedModule.IsExternalLibraryImport,
                        ElideOnDepth = isJsFileFromNodeModules,
                        IncludeReason = new FileIncludeReason
                        {
                            Kind = FileIncludeKind.Import,
                            Data = new ReferencedFileData
                            {
                                File = t.PathValue,
                                Index = importIndex,
                                Synthetic = importIndex < 0 ? entry : null,
                            },
                        },
                        PackageId = resolvedModule.PackageId,
                    }, null);
                }
            }

            t.ResolutionsInFile = resolutionsInFile;
            t.ResolutionsTrace = resolutionsTrace;
        }
    }

    // ast/utilities.go:2752-2773
    internal static string GetJSXImplicitImportBase(CompilerOptions compilerOptions, SourceFile? file)
    {
        var jsxImportSourcePragma = GetPragmaFromSourceFile(file, "jsximportsource");
        var jsxRuntimePragma = GetPragmaFromSourceFile(file, "jsxruntime");
        if (GetPragmaArgument(jsxRuntimePragma, "factory") == "classic")
        {
            return "";
        }
        if (compilerOptions.Jsx == JsxEmit.ReactJSX ||
            compilerOptions.Jsx == JsxEmit.ReactJSXDev ||
            compilerOptions.JsxImportSource != "" ||
            jsxImportSourcePragma is not null ||
            GetPragmaArgument(jsxRuntimePragma, "factory") == "automatic")
        {
            var result = GetPragmaArgument(jsxImportSourcePragma, "factory");
            if (result == "")
            {
                result = compilerOptions.JsxImportSource;
            }
            if (result == "")
            {
                result = "react";
            }
            return result;
        }
        return "";
    }

    // ast/utilities.go:2775-2780
    internal static string GetJSXRuntimeImport(string @base, CompilerOptions options)
    {
        if (@base == "")
        {
            return @base;
        }
        return @base + "/" + (options.Jsx == JsxEmit.ReactJSXDev ? "jsx-dev-runtime" : "jsx-runtime");
    }

    // ast/utilities.go:2782-2792
    private static Pragma? GetPragmaFromSourceFile(SourceFile? file, string name)
    {
        Pragma? result = null;
        if (file is not null)
        {
            foreach (var pragma in file.Pragmas ?? [])
            {
                if (pragma.Name == name)
                {
                    result = pragma; // Last one wins
                }
            }
        }
        return result;
    }

    // ast/utilities.go:2794-2801
    private static string GetPragmaArgument(Pragma? pragma, string name)
    {
        if (pragma is not null)
        {
            if (pragma.Value.Args is not null && pragma.Value.Args.TryGetValue(name, out var arg))
            {
                return arg.Value;
            }
        }
        return "";
    }

    // fileloader.go:634-642
    private Node CreateSyntheticImport(string text, SourceFile file)
    {
        lock (factoryMu)
        {
            var externalHelpersModuleReference = factory.NewStringLiteral(text, global::Ts.Ast.TokenFlags.None);
            var importDecl = factory.NewImportDeclaration(null, null, externalHelpersModuleReference, null);
            externalHelpersModuleReference.Parent = importDecl;
            importDecl.Parent = file.AsNode();
            return externalHelpersModuleReference;
        }
    }

    // fileloader.go:644-668
    internal LibFile PathForLibFile(string name)
    {
        if (PathForLibFileCache.TryLoad(name, out var cached))
        {
            return cached!;
        }

        var path = global::Ts.TsPath.TsPath.CombinePaths(DefaultLibraryPath, name);
        var replaced = false;
        if (Opts.Config.CompilerOptions()!.LibReplacement.IsTrue() && name != "lib.d.ts")
        {
            var libraryName = GetLibraryNameFromLibFileName(name);
            var resolveFrom = GetInferredLibraryNameResolveFrom(Opts.Config.CompilerOptions()!, Opts.Host.GetCurrentDirectory(), name);
            var (resolution, trace) = ResolveLibrary(libraryName, resolveFrom);
            if (resolution.IsResolved())
            {
                path = resolution.ResolvedFileName;
                replaced = true;
            }
            PathForLibFileResolutions.LoadOrStore(ToPath(resolveFrom), new LibResolution
            {
                LibraryName = libraryName,
                Resolution = resolution,
                Trace = trace,
            });
        }

        var (libPath, _) = PathForLibFileCache.LoadOrStore(name, new LibFile(name, path, replaced));
        return libPath;
    }

    // fileloader.go:670-675
    private (ResolvedModule Resolution, List<DiagAndArgs>? Trace) ResolveLibrary(string libraryName, string resolveFrom)
    {
        return Resolver.ResolveModuleName(libraryName, resolveFrom, ModuleKind.CommonJS, null);
    }

    // fileloader.go:677-698
    private static string GetLibraryNameFromLibFileName(string libFileName)
    {
        var components = libFileName.Split('.');
        var path = new System.Text.StringBuilder();
        path.Append("@typescript/lib-");
        if (components.Length > 1)
        {
            path.Append(components[1]);
        }
        var i = 2;
        while (i < components.Length && components[i] != "" && components[i] != "d")
        {
            if (i == 2)
            {
                path.Append('/');
            }
            else
            {
                path.Append('-');
            }
            path.Append(components[i]);
            i++;
        }
        return path.ToString();
    }

    // fileloader.go:700-708
    private static string GetInferredLibraryNameResolveFrom(CompilerOptions options, string currentDirectory, string libFileName)
    {
        string containingDirectory;
        if (options.ConfigFilePath != "")
        {
            containingDirectory = global::Ts.TsPath.TsPath.GetDirectoryPath(options.ConfigFilePath);
        }
        else
        {
            containingDirectory = currentDirectory;
        }
        return global::Ts.TsPath.TsPath.CombinePaths(containingDirectory, "__lib_node_modules_lookup_" + libFileName + "__.ts");
    }

    // fileloader.go:710-716
    private static ResolutionMode GetModeForTypeReferenceDirectiveInFile(FileReference reference, SourceFile file, SourceFileMetaData meta, CompilerOptions options)
    {
        if (reference.ResolutionMode != global::Ts.Core.Core.ResolutionModeNone)
        {
            return reference.ResolutionMode;
        }
        return GetDefaultResolutionModeForFile(file.FileName(), meta, options);
    }

    // fileloader.go:718-724
    internal static ResolutionMode GetDefaultResolutionModeForFile(string fileName, SourceFileMetaData meta, CompilerOptions options)
    {
        if (ImportSyntaxAffectsModuleResolution(options))
        {
            return ModuleFormat.GetImpliedNodeFormatForEmitWorker(fileName, options.GetEmitModuleKind(), meta);
        }
        return global::Ts.Core.Core.ResolutionModeNone;
    }

    // fileloader.go:726-756
    internal static ResolutionMode GetModeForUsageLocation(string fileName, SourceFileMetaData meta, Node usage, CompilerOptions? options)
    {
        var parent = usage.Parent!;
        if (NodeTests.IsImportDeclaration(parent) || parent.Kind == SyntaxKind.JSImportDeclaration || NodeTests.IsExportDeclaration(parent) || NodeTests.IsJSDocImportTag(parent))
        {
            var isTypeOnly = AstUtilities.IsExclusivelyTypeOnlyImportOrExport(parent);
            if (isTypeOnly)
            {
                Node? attributes = parent.Kind switch
                {
                    SyntaxKind.ImportDeclaration or SyntaxKind.JSImportDeclaration => parent.AsImportDeclaration().Attributes,
                    SyntaxKind.ExportDeclaration => parent.AsExportDeclaration().Attributes,
                    SyntaxKind.JSDocImportTag => parent.AsJSDocImportTag().Attributes,
                    _ => null,
                };
                var (over, ok) = ImportAttributesResolutionModeOverride(attributes);
                if (ok)
                {
                    return over;
                }
            }
        }
        if (NodeTests.IsLiteralTypeNode(parent) && parent.Parent is not null && NodeTests.IsImportTypeNode(parent.Parent))
        {
            var (over, ok) = ImportAttributesResolutionModeOverride(parent.Parent.AsImportTypeNode().Attributes);
            if (ok)
            {
                return over;
            }
        }

        if (options is not null && ImportSyntaxAffectsModuleResolution(options))
        {
            return GetEmitSyntaxForUsageLocationWorker(fileName, meta, usage, options);
        }

        return global::Ts.Core.Core.ResolutionModeNone;
    }

    // ast.go:2215 — ImportAttributesNode.GetResolutionModeOverride() (nil-receiver → static helper).
    // Grammar-error reporting (the optional grammarErrorOnNode callback) is elided here, matching Go's
    // nil-callback call sites; the checker's GetResolutionModeOverride(ImportAttributes, reportErrors)
    // owns the reporting path.
    // Delegates to the faithful ast-level home (ast.go:2215) now that AstUtilities owns it.
    private static (ResolutionMode, bool) ImportAttributesResolutionModeOverride(Node? node) =>
        AstUtilities.GetResolutionModeOverride(node);

    // fileloader.go:758-762
    private static bool ImportSyntaxAffectsModuleResolution(CompilerOptions options)
    {
        var moduleResolution = options.GetModuleResolutionKind();
        return (ModuleResolutionKind.Node16 <= moduleResolution && moduleResolution <= ModuleResolutionKind.NodeNext) ||
            options.GetResolvePackageJsonExports() || options.GetResolvePackageJsonImports();
    }

    // fileloader.go:764-791
    internal static ResolutionMode GetEmitSyntaxForUsageLocationWorker(string fileName, SourceFileMetaData meta, Node usage, CompilerOptions options)
    {
        var parent = usage.Parent!;
        if (AstUtilities.IsRequireCall(parent, false /*requireStringLiteralLikeArgument*/) ||
            (NodeTests.IsExternalModuleReference(parent) && parent.Parent is not null && NodeTests.IsImportEqualsDeclaration(parent.Parent)))
        {
            return ModuleKind.CommonJS;
        }
        var fileEmitMode = ModuleFormat.GetEmitModuleFormatOfFileWorker(fileName, options, meta);
        var walked = AstUtilities.WalkUpParenthesizedExpressions(parent);
        if (walked is not null && IsImportCall(walked))
        {
            return ShouldTransformImportCall(fileName, options, fileEmitMode) ? ModuleKind.CommonJS : ModuleKind.ESNext;
        }
        if (fileEmitMode == ModuleKind.CommonJS)
        {
            return ModuleKind.CommonJS;
        }
        if (fileEmitMode.IsNonNodeESM() || fileEmitMode == ModuleKind.Preserve)
        {
            return ModuleKind.ESNext;
        }
        return global::Ts.Core.Core.ResolutionModeNone;
    }

    // utilities.go:IsImportCall
    private static bool IsImportCall(Node node)
    {
        // PORT-NOTE (phase 4/5): the `import defer(...)` meta-property sub-clause is deferred
        // (IsMetaProperty/MetaProperty accessors not yet ported); the plain `import(...)` form is exact.
        return NodeTests.IsCallExpression(node) && node.Expression()?.Kind == SyntaxKind.ImportKeyword;
    }

    // utilities.go:3687-3693
    private static bool ShouldTransformImportCall(string fileName, CompilerOptions options, ModuleKind impliedNodeFormatForEmit)
    {
        _ = fileName;
        var moduleKind = options.GetEmitModuleKind();
        if ((ModuleKind.Node16 <= moduleKind && moduleKind <= ModuleKind.NodeNext) || moduleKind == ModuleKind.Preserve)
        {
            return false;
        }
        return impliedNodeFormatForEmit < ModuleKind.ES2015;
    }

    // Bridge a ParsedCommandLine redirect to module.ResolvedProjectReference — in Go the
    // *tsoptions.ParsedCommandLine satisfies the interface directly (fileloader.go:583 passes
    // redirect verbatim); ParsedCommandLine implements it on the C# side too.
    private static ResolvedProjectReference? AsResolvedProjectReference(global::Ts.TsOptions.ParsedCommandLine? redirect) => redirect;
}
