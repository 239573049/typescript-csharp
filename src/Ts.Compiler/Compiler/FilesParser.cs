// Ported from tsgo internal/compiler/filesparser.go.
//
// This is the parallel file-discovery engine: it walks root parse tasks, dedups by path via a
// SyncMap, loads each file (parse + resolve imports/refs), enqueues discovered subtasks, and
// finally assembles the immutable processedFiles snapshot. This is the real fileloader→module
// pipeline; it is ported faithfully.
//
// §1.5 (namespace shadowing): sibling-namespace static classes are fully qualified with global::.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.Module;

namespace Ts.Compiler;

// filesparser.go:19-47
internal sealed class ParseTask : HasFileName
{
    internal string NormalizedFilePath = "";
    internal global::Ts.TsPath.Path PathValue;
    internal SourceFile? File;
    internal LibFile? LibFile;
    internal ParseTask? RedirectedParseTask;
    internal List<ParseTask>? SubTasks;
    internal bool Loaded;
    internal bool StartedSubTasks;
    internal bool IsForAutomaticTypeDirective;
    internal FileIncludeReason? IncludeReason;
    internal PackageId PackageId;

    internal SourceFileMetaData Metadata;
    internal ModeAwareCache<ResolvedModule>? ResolutionsInFile;
    internal List<DiagAndArgs>? ResolutionsTrace;
    internal ModeAwareCache<ResolvedTypeReferenceDirective>? TypeResolutionsInFile;
    internal List<DiagAndArgs>? TypeResolutionsTrace;
    internal List<Diagnostic>? ResolutionDiagnostics;
    internal List<ProcessingDiagnostic>? ProcessingDiagnostics;
    internal Node? ImportHelpersImportSpecifier;
    internal JsxRuntimeImportSpecifier? JsxRuntimeImportSpecifier;

    internal bool IncreaseDepth;
    internal bool ElideOnDepth;

    internal ParseTask? LoadedTask;
    internal List<FileIncludeReason>? AllIncludeReasons;

    // filesparser.go:49-51
    public string FileName() => NormalizedFilePath;

    // filesparser.go:53-55
    public global::Ts.TsPath.Path Path() => PathValue;

    // filesparser.go:57-159
    internal void Load(FileLoader loader)
    {
        Loaded = true;
        if (IsForAutomaticTypeDirective)
        {
            LoadAutomaticTypeDirectives(loader);
            return;
        }
        var redirect = loader.ProjectReferenceFileMapper.GetParseFileRedirect(this);
        if (redirect != "")
        {
            Redirect(loader, redirect);
            return;
        }

        if (global::Ts.TsPath.TsPath.HasExtension(NormalizedFilePath))
        {
            var opts = loader.Opts.Config.CompilerOptions()!;
            var allowNonTsExtensions = opts.AllowNonTsExtensions.IsTrue();
            if (!allowNonTsExtensions)
            {
                var canonicalFileName = global::Ts.TsPath.TsPath.GetCanonicalFileName(NormalizedFilePath, loader.Opts.Host.FS().UseCaseSensitiveFileNames());
                if (!loader.IsSupportedExtension(canonicalFileName))
                {
                    if (global::Ts.TsPath.TsPath.HasJSFileExtension(canonicalFileName))
                    {
                        AddProcessingDiagnostic(new ProcessingDiagnostic
                        {
                            Kind = ProcessingDiagnosticKind.ExplainingFileInclude,
                            Data = new IncludeExplainingDiagnostic
                            {
                                DiagnosticReason = IncludeReason,
                                Message = global::Ts.Diagnostics.Diagnostics.File_0_is_a_JavaScript_file_Did_you_mean_to_enable_the_allowJs_option,
                                Args = [NormalizedFilePath],
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
                                DiagnosticReason = IncludeReason,
                                Message = global::Ts.Diagnostics.Diagnostics.File_0_has_an_unsupported_extension_The_only_supported_extensions_are_1,
                                Args = [NormalizedFilePath, "'" + string.Join("', '", global::Ts.Core.CoreUtil.Flatten(loader.SupportedExtensions) ?? []) + "'"],
                            },
                        });
                    }
                    return;
                }
            }
        }

        Interlocked.Increment(ref loader.TotalFileCount);
        if (LibFile is not null)
        {
            Interlocked.Increment(ref loader.LibFileCount);
            // Default lib files are all scripts; we can safely skip looking up their package.json.
            Metadata = new SourceFileMetaData { ImpliedNodeFormat = global::Ts.Core.Core.ResolutionModeCommonJS };
        }
        else
        {
            Metadata = loader.LoadSourceFileMetaData(NormalizedFilePath);
        }

        var file = loader.ParseSourceFile(this);
        if (file is null)
        {
            return;
        }

        File = file;
        SubTasks = new List<ParseTask>((file.ReferencedFiles?.Length ?? 0) + (file.Imports()?.Length ?? 0) + (file.ModuleAugmentations?.Length ?? 0));

        var compilerOptions = loader.Opts.Config.CompilerOptions()!;
        if (!compilerOptions.NoResolve.IsTrue())
        {
            if (file.ReferencedFiles is not null)
            {
                for (int index = 0; index < file.ReferencedFiles.Length; index++)
                {
                    var refName = file.ReferencedFiles[index].FileName;
                    var (resolvedRef, processingDiagnostic) = loader.ResolveTripleslashPathReference(refName, file.FileName(), index);
                    if (processingDiagnostic is not null)
                    {
                        AddProcessingDiagnostic(processingDiagnostic);
                        continue;
                    }
                    AddSubTask(resolvedRef!, null);
                }
            }

            loader.ResolveTypeReferenceDirectives(this);
        }

        if (compilerOptions.NoLib != Tristate.True && file.LibReferenceDirectives is not null)
        {
            for (int index = 0; index < file.LibReferenceDirectives.Length; index++)
            {
                var lib = file.LibReferenceDirectives[index];
                var includeReason = new FileIncludeReason
                {
                    Kind = FileIncludeKind.LibReferenceDirective,
                    Data = new ReferencedFileData { File = PathValue, Index = index },
                };
                if (global::Ts.TsOptions.EnumMaps.TryGetLibFileName(lib.FileName, out var name))
                {
                    var libFile = loader.PathForLibFile(name);
                    AddSubTask(new ResolvedRef { FileName = libFile.PathValue, IncludeReason = includeReason }, libFile);
                }
                else
                {
                    AddProcessingDiagnostic(new ProcessingDiagnostic
                    {
                        Kind = ProcessingDiagnosticKind.UnknownReference,
                        Data = includeReason,
                    });
                }
            }
        }

        loader.ResolveImportsAndModuleAugmentations(this);
    }

    // filesparser.go:161-169
    private void Redirect(FileLoader loader, string fileName)
    {
        _ = loader;
        RedirectedParseTask = new ParseTask
        {
            NormalizedFilePath = global::Ts.TsPath.TsPath.NormalizePath(fileName),
            LibFile = LibFile,
            IncludeReason = IncludeReason,
        };
        // increaseDepth and elideOnDepth are not copied to redirects, otherwise their depth would be double counted.
        SubTasks = new List<ParseTask> { RedirectedParseTask };
    }

    // filesparser.go:171-182
    private void LoadAutomaticTypeDirectives(FileLoader loader)
    {
        var (toParseTypeRefs, typeResolutionsInFile, typeResolutionsTrace, pDiagnostics) = loader.ResolveAutomaticTypeDirectives(NormalizedFilePath);
        TypeResolutionsInFile = typeResolutionsInFile;
        TypeResolutionsTrace = typeResolutionsTrace;
        if (pDiagnostics is not null)
        {
            AddProcessingDiagnostic(pDiagnostics.ToArray());
        }
        foreach (var typeResolution in toParseTypeRefs)
        {
            AddSubTask(typeResolution, null);
        }
    }

    // filesparser.go:192-203
    internal void AddSubTask(ResolvedRef reference, LibFile? libFile)
    {
        var normalizedFilePath = global::Ts.TsPath.TsPath.NormalizePath(reference.FileName);
        var subTask = new ParseTask
        {
            NormalizedFilePath = normalizedFilePath,
            LibFile = libFile,
            IncreaseDepth = reference.IncreaseDepth,
            ElideOnDepth = reference.ElideOnDepth,
            IncludeReason = reference.IncludeReason,
            PackageId = reference.PackageId,
        };
        (SubTasks ??= new List<ParseTask>()).Add(subTask);
    }

    internal void AddProcessingDiagnostic(params ProcessingDiagnostic[] d)
    {
        (ProcessingDiagnostics ??= new List<ProcessingDiagnostic>()).AddRange(d);
    }
}

// filesparser.go:184-190
internal sealed class ResolvedRef
{
    internal string FileName = "";
    internal bool IncreaseDepth;
    internal bool ElideOnDepth;
    internal FileIncludeReason? IncludeReason;
    internal PackageId PackageId;
}

// filesparser.go:231-238
internal sealed class ParseTaskData
{
    // map of tasks by file casing
    internal readonly Dictionary<string, ParseTask> Tasks = new();
    internal readonly Lock Mu = new();
    internal int LowestDepth = int.MaxValue;
    internal bool StartedSubTasks;
    internal PackageId PackageId;

    internal ParseTaskData(ParseTask task)
    {
        Tasks[task.NormalizedFilePath] = task;
    }
}

// filesparser.go:205-209
internal sealed class FilesParser
{
    internal IWorkGroup Wg = null!;
    internal readonly SyncMap<global::Ts.TsPath.Path, ParseTaskData> TaskDataByPath = new();
    internal int MaxDepth;

    // filesparser.go:240-243
    internal void Parse(FileLoader loader, List<ParseTask> tasks)
    {
        Start(loader, tasks, 0);
        Wg.RunAndWait();
    }

    // filesparser.go:245-304
    internal void Start(FileLoader loader, List<ParseTask> tasks, int depth)
    {
        for (int i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            int idx = i;
            var tasksList = tasks;
            task.PathValue = loader.ToPath(task.NormalizedFilePath);
            var candidate = new ParseTaskData(task);
            var (data, loaded) = TaskDataByPath.LoadOrStore(task.PathValue, candidate);

            Wg.Queue(() =>
            {
                lock (data.Mu)
                {
                    var startSubtasks = false;
                    if (loaded)
                    {
                        if (data.Tasks.TryGetValue(task.NormalizedFilePath, out var existingTask))
                        {
                            tasksList[idx].LoadedTask = existingTask;
                        }
                        else
                        {
                            data.Tasks[task.NormalizedFilePath] = task;
                            // New task for file name - so load subtasks if there was loading for any other casing.
                            startSubtasks = data.StartedSubTasks;
                        }
                    }

                    // Propagate packageId to data if we have one and data doesn't yet.
                    // PORT-NOTE: Go's PackageId.Name zero value is ""; C# default(PackageId).Name is null.
                    // Treat null-or-empty as Go's "" so no-package files don't collide on an empty PackageId.
                    if (string.IsNullOrEmpty(data.PackageId.Name) && !string.IsNullOrEmpty(task.PackageId.Name))
                    {
                        data.PackageId = task.PackageId;
                    }

                    var currentDepth = task.IncreaseDepth ? depth + 1 : depth;
                    if (currentDepth < data.LowestDepth)
                    {
                        // If we're seeing this task at a lower depth than before, reprocess its subtasks.
                        data.LowestDepth = currentDepth;
                        startSubtasks = true;
                        data.StartedSubTasks = true;
                    }

                    if (task.ElideOnDepth && currentDepth > MaxDepth)
                    {
                        return;
                    }

                    // map-iter: 序无关 —— each casing entry is loaded independently; ordering does not
                    // affect the resulting file set (per-path work is idempotent under data.Mu).
                    foreach (var taskByFileName in data.Tasks.Values)
                    {
                        var loadSubTasks = startSubtasks;
                        if (!taskByFileName.Loaded)
                        {
                            taskByFileName.Load(loader);
                            if (taskByFileName.RedirectedParseTask is not null)
                            {
                                // Always load redirected task.
                                loadSubTasks = true;
                                data.StartedSubTasks = true;
                            }
                        }
                        if (!taskByFileName.StartedSubTasks && loadSubTasks)
                        {
                            taskByFileName.StartedSubTasks = true;
                            Start(loader, taskByFileName.SubTasks ?? [], data.LowestDepth);
                        }
                    }
                }
            });
        }
    }

    // filesparser.go:557-567
    internal void AddIncludeReason(IncludeProcessor includeProcessor, ParseTask task, FileIncludeReason reason)
    {
        if (task.RedirectedParseTask is not null)
        {
            AddIncludeReason(includeProcessor, task.RedirectedParseTask, reason);
        }
        else if (task.Loaded)
        {
            if (includeProcessor.FileIncludeReasons.TryGetValue(task.PathValue, out var existing))
            {
                existing.Add(reason);
            }
            else
            {
                includeProcessor.FileIncludeReasons[task.PathValue] = new List<FileIncludeReason> { reason };
            }
        }
    }

    // filesparser.go:306-555
    internal ProcessedFiles GetProcessedFiles(FileLoader loader)
    {
        var totalFileCount = loader.TotalFileCount;
        var libFileCount = loader.LibFileCount;

        var missingFiles = new List<string>();
        var duplicateSourceFiles = new List<DuplicateSourceFile>();
        var files = new List<SourceFile>(Math.Max(totalFileCount - libFileCount, 0));
        var libFiles = new List<SourceFile>(totalFileCount);

        var filesByPath = new Dictionary<global::Ts.TsPath.Path, SourceFile>(totalFileCount);
        // 'filename -> file association' ignoring case, to track two file names differing only in casing.
        Dictionary<string, ParseTask>? tasksSeenByNameIgnoreCase = null;
        if (loader.ComparePathsOptions.UseCaseSensitiveFileNames)
        {
            tasksSeenByNameIgnoreCase = new Dictionary<string, ParseTask>(totalFileCount);
        }

        var includeProcessor = new IncludeProcessor();
        Dictionary<global::Ts.TsPath.Path, string>? outputFileToProjectReferenceSource = null;
        if (!loader.Opts.CanUseProjectReferenceSource())
        {
            outputFileToProjectReferenceSource = new Dictionary<global::Ts.TsPath.Path, string>(totalFileCount);
        }
        var resolvedModules = new Dictionary<global::Ts.TsPath.Path, ModeAwareCache<ResolvedModule>>(totalFileCount + 1);
        var typeResolutionsInFile = new Dictionary<global::Ts.TsPath.Path, ModeAwareCache<ResolvedTypeReferenceDirective>>(totalFileCount);
        var sourceFileMetaDatas = new Dictionary<global::Ts.TsPath.Path, SourceFileMetaData>(totalFileCount);
        Dictionary<global::Ts.TsPath.Path, JsxRuntimeImportSpecifier>? jsxRuntimeImportSpecifiers = null;
        Dictionary<global::Ts.TsPath.Path, Node>? importHelpersImportSpecifiers = null;
        var sourceFilesFoundSearchingNodeModules = new Set<global::Ts.TsPath.Path>();
        var libFilesMap = new Dictionary<global::Ts.TsPath.Path, LibFile>(libFileCount);

        Dictionary<global::Ts.TsPath.Path, List<string>>? redirectTargetsMap = null;
        Dictionary<global::Ts.TsPath.Path, RedirectsFile>? redirectFilesByPath = null;
        Dictionary<PackageId, SourceFile>? packageIdToSourceFile = null;
        if (!loader.Opts.Config.CompilerOptions()!.DeduplicatePackages.IsFalse())
        {
            redirectTargetsMap = new Dictionary<global::Ts.TsPath.Path, List<string>>();
            packageIdToSourceFile = new Dictionary<PackageId, SourceFile>();
        }

        // recordedDuplicates tracks, per task data, the set of file-name casings already recorded.
        Dictionary<ParseTaskData, Set<string>>? recordedDuplicates = null;

        void CollectFiles(List<ParseTask> tasks, Dictionary<ParseTaskData, string> seen)
        {
            foreach (var taskEntry in tasks)
            {
                var task = taskEntry;
                var includeReason = task.IncludeReason;
                // Exclude automatic type directive tasks from include reason processing.
                if (task.RedirectedParseTask is null && !task.IsForAutomaticTypeDirective)
                {
                    if (task.LoadedTask is not null)
                    {
                        task = task.LoadedTask;
                    }
                    AddIncludeReason(includeProcessor, task, includeReason!);
                }
                TaskDataByPath.TryLoad(task.PathValue, out var data);
                if (!task.Loaded)
                {
                    continue;
                }

                // ensure we only walk each task once
                if (seen.TryGetValue(data!, out var checkedName))
                {
                    if (task.File is not null && checkedName != task.NormalizedFilePath)
                    {
                        recordedDuplicates ??= new Dictionary<ParseTaskData, Set<string>>();
                        if (!recordedDuplicates.TryGetValue(data!, out var dups))
                        {
                            dups = new Set<string>();
                            recordedDuplicates[data!] = dups;
                        }
                        if (dups.AddIfAbsent(task.NormalizedFilePath))
                        {
                            duplicateSourceFiles.Add(new DuplicateSourceFile
                            {
                                ParseOptions = task.File.ParseOptions(),
                                Hash = task.File.Hash,
                                ScriptKind = task.File.ScriptKind,
                            });
                        }
                    }
                    if (!loader.Opts.Config.CompilerOptions()!.ForceConsistentCasingInFileNames.IsFalse())
                    {
                        // Ignore differences in drive letters.
                        var checkedAbsolutePath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePathWithoutRoot(checkedName, loader.ComparePathsOptions.CurrentDirectory);
                        var inputAbsolutePath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePathWithoutRoot(task.NormalizedFilePath, loader.ComparePathsOptions.CurrentDirectory);
                        if (checkedAbsolutePath != inputAbsolutePath)
                        {
                            includeProcessor.AddProcessingDiagnosticsForFileCasing(task.PathValue, checkedName, task.NormalizedFilePath, includeReason!);
                        }
                    }
                    continue;
                }
                seen[data!] = task.NormalizedFilePath;

                if (tasksSeenByNameIgnoreCase is not null)
                {
                    var pathLowerCase = global::Ts.TsPath.TsPath.ToFileNameLowerCase(task.PathValue.ToString());
                    if (tasksSeenByNameIgnoreCase.TryGetValue(pathLowerCase, out var taskByIgnoreCase))
                    {
                        includeProcessor.AddProcessingDiagnosticsForFileCasing(taskByIgnoreCase.PathValue, taskByIgnoreCase.NormalizedFilePath, task.NormalizedFilePath, includeReason!);
                    }
                    else
                    {
                        tasksSeenByNameIgnoreCase[pathLowerCase] = task;
                    }
                }

                if (task.TypeResolutionsTrace is not null)
                {
                    foreach (var trace in task.TypeResolutionsTrace)
                    {
                        loader.Opts.Host.Trace(trace.Message, trace.Args);
                    }
                }
                if (task.ResolutionsTrace is not null)
                {
                    foreach (var trace in task.ResolutionsTrace)
                    {
                        loader.Opts.Host.Trace(trace.Message, trace.Args);
                    }
                }

                var file = task.File;
                if (packageIdToSourceFile is not null && !string.IsNullOrEmpty(data!.PackageId.Name))
                {
                    if (packageIdToSourceFile.TryGetValue(data.PackageId, out var packageIdFile))
                    {
                        if (file is not null)
                        {
                            duplicateSourceFiles.Add(new DuplicateSourceFile
                            {
                                ParseOptions = file.ParseOptions(),
                                Hash = file.Hash,
                                ScriptKind = file.ScriptKind,
                            });
                        }
                        if (!redirectTargetsMap!.TryGetValue(packageIdFile.Path(), out var targetList))
                        {
                            targetList = new List<string>();
                            redirectTargetsMap[packageIdFile.Path()] = targetList;
                        }
                        targetList.Add(task.NormalizedFilePath);
                        redirectFilesByPath ??= new Dictionary<global::Ts.TsPath.Path, RedirectsFile>(totalFileCount);
                        redirectFilesByPath[task.PathValue] = new RedirectsFile
                        {
                            Index = files.Count + redirectFilesByPath.Count,
                            FileNameValue = task.NormalizedFilePath,
                            PathValue = task.PathValue,
                            Target = packageIdFile.Path(),
                        };
                        filesByPath[task.PathValue] = packageIdFile;
                        if (data.LowestDepth > 0)
                        {
                            sourceFilesFoundSearchingNodeModules.Add(task.PathValue);
                        }
                        continue;
                    }
                    if (file is not null)
                    {
                        packageIdToSourceFile[data.PackageId] = file;
                    }
                }

                if (task.SubTasks is { Count: > 0 })
                {
                    CollectFiles(task.SubTasks, seen);
                }

                if (task.RedirectedParseTask is not null)
                {
                    if (!loader.Opts.CanUseProjectReferenceSource())
                    {
                        outputFileToProjectReferenceSource![task.RedirectedParseTask.PathValue] = task.FileName();
                    }
                    continue;
                }

                if (task.IsForAutomaticTypeDirective)
                {
                    typeResolutionsInFile[task.PathValue] = task.TypeResolutionsInFile ?? new ModeAwareCache<ResolvedTypeReferenceDirective>();
                    if (task.ProcessingDiagnostics is { Count: > 0 })
                    {
                        includeProcessor.ProcessingDiagnostics.AddRange(task.ProcessingDiagnostics);
                    }
                    continue;
                }

                var path = task.PathValue;

                if (task.ProcessingDiagnostics is { Count: > 0 })
                {
                    includeProcessor.ProcessingDiagnostics.AddRange(task.ProcessingDiagnostics);
                }

                if (file is null)
                {
                    missingFiles.Add(task.NormalizedFilePath);
                    continue;
                }

                if (task.LibFile is not null)
                {
                    libFiles.Add(file);
                    libFilesMap[path] = task.LibFile;
                }
                else
                {
                    files.Add(file);
                }
                filesByPath[path] = file;
                resolvedModules[path] = task.ResolutionsInFile ?? new ModeAwareCache<ResolvedModule>();
                typeResolutionsInFile[path] = task.TypeResolutionsInFile ?? new ModeAwareCache<ResolvedTypeReferenceDirective>();
                sourceFileMetaDatas[path] = task.Metadata;

                if (task.JsxRuntimeImportSpecifier is not null)
                {
                    jsxRuntimeImportSpecifiers ??= new Dictionary<global::Ts.TsPath.Path, JsxRuntimeImportSpecifier>(totalFileCount);
                    jsxRuntimeImportSpecifiers[path] = task.JsxRuntimeImportSpecifier;
                }
                if (task.ImportHelpersImportSpecifier is not null)
                {
                    importHelpersImportSpecifiers ??= new Dictionary<global::Ts.TsPath.Path, Node>(totalFileCount);
                    importHelpersImportSpecifiers[path] = task.ImportHelpersImportSpecifier;
                }
                if (data!.LowestDepth > 0)
                {
                    sourceFilesFoundSearchingNodeModules.Add(path);
                }
            }
        }

        CollectFiles(loader.RootTasks, new Dictionary<ParseTaskData, string>(totalFileCount));
        loader.SortLibs(libFiles);

        var allFiles = new List<SourceFile>(libFiles.Count + files.Count);
        allFiles.AddRange(libFiles);
        allFiles.AddRange(files);
        if (redirectFilesByPath is not null)
        {
            // map-iter: 序无关 —— shifting every redirect file's index by the lib-file count.
            foreach (var redirectFile in redirectFilesByPath.Values)
            {
                redirectFile.Index += libFiles.Count;
            }
        }

        // pathForLibFileResolutions: sorted-key iteration (filesparser.go:523-533).
        var libResolutionKeys = new List<global::Ts.TsPath.Path>();
        foreach (var key in loader.PathForLibFileResolutions.Keys())
        {
            libResolutionKeys.Add(key);
        }
        libResolutionKeys.Sort();
        foreach (var key in libResolutionKeys)
        {
            loader.PathForLibFileResolutions.TryLoad(key, out var value);
            resolvedModules[key] = new ModeAwareCache<ResolvedModule>
            {
                [new ModeAwareCacheKey(value!.LibraryName, ModuleKind.CommonJS)] = value.Resolution!,
            };
            if (value.Trace is not null)
            {
                foreach (var trace in value.Trace)
                {
                    loader.Opts.Host.Trace(trace.Message, trace.Args);
                }
            }
        }

        return new ProcessedFiles
        {
            FinishedProcessing = true,
            Resolver = loader.Resolver,
            Files = allFiles.ToArray(),
            DuplicateSourceFiles = duplicateSourceFiles.ToArray(),
            FilesByPath = filesByPath,
            ProjectReferenceFileMapper = loader.ProjectReferenceFileMapper,
            ResolvedModules = resolvedModules,
            TypeResolutionsInFile = typeResolutionsInFile,
            SourceFileMetaDatas = sourceFileMetaDatas,
            JsxRuntimeImportSpecifiers = jsxRuntimeImportSpecifiers,
            ImportHelpersImportSpecifiers = importHelpersImportSpecifiers,
            SourceFilesFoundSearchingNodeModules = sourceFilesFoundSearchingNodeModules,
            LibFiles = libFilesMap,
            MissingFiles = missingFiles.ToArray(),
            IncludeProcessor = includeProcessor,
            OutputFileToProjectReferenceSource = outputFileToProjectReferenceSource,
            RedirectTargetsMap = redirectTargetsMap,
            RedirectFilesByPath = redirectFilesByPath,
        };
    }
}
