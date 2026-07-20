// Ported from tsgo internal/execute/incremental/snapshottobuildinfo.go.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ts.Collections;
using Ts.Core;

namespace Ts.Execute.Incremental;

using PathT = global::Ts.TsPath.Path;
using TsPathApi = global::Ts.TsPath.TsPath;

internal static class SnapshotToBuildInfoConverter
{
    // snapshottobuildinfo.go:18-57
    internal static BuildInfo SnapshotToBuildInfo(Snapshot snapshot, global::Ts.Compiler.Program program, string buildInfoFileName)
    {
        var buildInfo = new BuildInfo
        {
            Version = global::Ts.Core.TsVersion.Version(),
        };
        var to = new ToBuildInfo
        {
            snapshot = snapshot,
            program = program,
            buildInfo = buildInfo,
            buildInfoDirectory = TsPathApi.GetDirectoryPath(buildInfoFileName),
            comparePathsOptions = new global::Ts.TsPath.ComparePathsOptions
            {
                CurrentDirectory = program.GetCurrentDirectory(),
                UseCaseSensitiveFileNames = program.UseCaseSensitiveFileNames(),
            },
        };

        if (snapshot.options.IsIncremental())
        {
            to.CollectRootFiles();
            to.SetFileInfoAndEmitSignatures();
            to.SetRootOfIncrementalProgram();
            to.SetCompilerOptions();
            to.SetReferencedMap();
            to.SetChangeFileSet();
            to.SetSemanticDiagnostics();
            to.SetEmitDiagnostics();
            to.SetAffectedFilesPendingEmit();
            if (snapshot.latestChangedDtsFile.Length > 0)
            {
                buildInfo.LatestChangedDtsFile = to.RelativeToBuildInfo(snapshot.latestChangedDtsFile);
            }
        }
        else
        {
            to.SetRootOfNonIncrementalProgram();
        }
        buildInfo.Errors = snapshot.hasErrors.IsTrue();
        buildInfo.SemanticErrors = snapshot.hasSemanticErrors;
        buildInfo.CheckPending = snapshot.checkPending;
        to.SetPackageJsons();
        return buildInfo;
    }
}

// snapshottobuildinfo.go:59-68
internal sealed class ToBuildInfo
{
    internal Snapshot snapshot = null!;
    internal global::Ts.Compiler.Program program = null!;
    internal BuildInfo buildInfo = null!;
    internal string buildInfoDirectory = "";
    internal global::Ts.TsPath.ComparePathsOptions comparePathsOptions;
    internal readonly Dictionary<string, int> fileNameToFileId = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, int> fileNamesToFileIdListId = new(StringComparer.Ordinal);
    internal readonly Dictionary<global::Ts.Ast.SourceFile, PathT> roots = new();
    private List<string>? fileNames;
    private List<int[]>? fileIdsList;

    // snapshottobuildinfo.go:70-72
    internal string RelativeToBuildInfo(string path) =>
        TsPathApi.EnsurePathIsNonModuleName(TsPathApi.GetRelativePathFromDirectory(buildInfoDirectory, path, comparePathsOptions));

    // snapshottobuildinfo.go:74-86
    internal int ToFileId(PathT path)
    {
        var fileId = fileNameToFileId.GetValueOrDefault(path.ToString());
        if (fileId == 0)
        {
            fileNames ??= new List<string>();
            var libFile = program.GetDefaultLibFile(path);
            if (libFile != null && !libFile.Replaced)
            {
                fileNames.Add(libFile.Name);
            }
            else
            {
                fileNames.Add(RelativeToBuildInfo(path.ToString()));
            }
            buildInfo.FileNames = fileNames.ToArray();
            fileId = fileNames.Count;
            fileNameToFileId[path.ToString()] = fileId;
        }
        return fileId;
    }

    // snapshottobuildinfo.go:88-102
    internal int ToFileIdListId(Set<PathT> set)
    {
        var fileIds = SetExtensions.Keys(set).Select(ToFileId).ToArray();
        Array.Sort(fileIds);
        var key = string.Join(",", fileIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));

        var fileIdListId = fileNamesToFileIdListId.GetValueOrDefault(key);
        if (fileIdListId == 0)
        {
            fileIdsList ??= new List<int[]>();
            fileIdsList.Add(fileIds);
            buildInfo.FileIdsList = fileIdsList.ToArray();
            fileIdListId = fileIdsList.Count;
            fileNamesToFileIdListId[key] = fileIdListId;
        }
        return fileIdListId;
    }

    // snapshottobuildinfo.go:104-117
    internal object? ToRelativeToBuildInfoCompilerOptionValue(global::Ts.TsOptions.CommandLineOption option, object? v)
    {
        if (option.Kind == "list")
        {
            if (option.Elements()!.IsFilePath)
            {
                if (v is string[] arr)
                {
                    return arr.Select(RelativeToBuildInfo).ToArray();
                }
            }
        }
        else if (option.IsFilePath)
        {
            if (v is string str && str.Length > 0)
            {
                return RelativeToBuildInfo(str);
            }
        }
        return v;
    }

    // snapshottobuildinfo.go:119-142
    internal BuildInfoDiagnostic[]? ToBuildInfoDiagnosticsFromFileNameDiagnostics(BuildInfoDiagnosticWithFileName[]? diagnostics)
    {
        if (diagnostics == null)
        {
            return null;
        }
        return diagnostics.Select(d =>
        {
            var file = 0;
            if (d.file.ToString().Length > 0)
            {
                file = ToFileId(d.file);
            }
            return new BuildInfoDiagnostic
            {
                File = file,
                NoFile = d.noFile,
                Pos = d.pos,
                End = d.end,
                Code = d.code,
                Category = d.category,
                MessageKey = d.messageKey,
                MessageArgs = d.messageArgs,
                MessageChain = ToBuildInfoDiagnosticsFromFileNameDiagnostics(d.messageChain),
                RelatedInformation = ToBuildInfoDiagnosticsFromFileNameDiagnostics(d.relatedInformation),
                ReportsUnnecessary = d.reportsUnnecessary,
                ReportsDeprecated = d.reportsDeprecated,
                SkippedOnNoEmit = d.skippedOnNoEmit,
                RepopulateInfo = ToBuildInfoRepopulateInfo(d.repopulateInfo),
            };
        }).ToArray();
    }

    // snapshottobuildinfo.go:144-170
    internal BuildInfoDiagnostic[]? ToBuildInfoDiagnosticsFromDiagnostics(PathT filePath, global::Ts.Ast.Diagnostic[]? diagnostics)
    {
        if (diagnostics == null)
        {
            return null;
        }
        return diagnostics.Select(d =>
        {
            var file = 0;
            var noFile = false;
            if (d.File() == null)
            {
                noFile = true;
            }
            else if (d.File()!.Path() != filePath)
            {
                file = ToFileId(d.File()!.Path());
            }
            return new BuildInfoDiagnostic
            {
                File = file,
                NoFile = noFile,
                Pos = d.Loc().Pos,
                End = d.Loc().End,
                Code = d.Code(),
                Category = d.Category(),
                MessageKey = d.MessageKey(),
                MessageArgs = d.MessageArgs(),
                MessageChain = ToBuildInfoDiagnosticsFromDiagnostics(filePath, d.MessageChain()),
                RelatedInformation = ToBuildInfoDiagnosticsFromDiagnostics(filePath, d.RelatedInformation()),
                ReportsUnnecessary = d.ReportsUnnecessary(),
                ReportsDeprecated = d.ReportsDeprecated(),
                SkippedOnNoEmit = d.SkippedOnNoEmit(),
                RepopulateInfo = ToBuildInfoRepopulateInfo(d.RepopulateInfo()),
            };
        }).ToArray();
    }

    // snapshottobuildinfo.go:172-182
    internal static BuildInfoRepopulateInfo? ToBuildInfoRepopulateInfo(global::Ts.Ast.RepopulateDiagnosticInfo? info)
    {
        if (info == null)
        {
            return null;
        }
        return new BuildInfoRepopulateInfo
        {
            Kind = info.Kind,
            ModuleReference = info.ModuleReference,
            Mode = info.Mode,
            PackageName = info.PackageName,
        };
    }

    // snapshottobuildinfo.go:184-198
    internal BuildInfoDiagnosticsOfFile? ToBuildInfoDiagnosticsOfFile(PathT filePath, DiagnosticsOrBuildInfoDiagnosticsWithFileName diags)
    {
        if (diags.diagnostics is { Length: > 0 })
        {
            return new BuildInfoDiagnosticsOfFile
            {
                FileId = ToFileId(filePath),
                Diagnostics = ToBuildInfoDiagnosticsFromDiagnostics(filePath, diags.diagnostics)!,
            };
        }
        if (diags.buildInfoDiagnostics is { Length: > 0 })
        {
            return new BuildInfoDiagnosticsOfFile
            {
                FileId = ToFileId(filePath),
                Diagnostics = ToBuildInfoDiagnosticsFromFileNameDiagnostics(diags.buildInfoDiagnostics)!,
            };
        }
        return null;
    }

    // snapshottobuildinfo.go:200-212
    internal void CollectRootFiles()
    {
        foreach (var fileName in program.CommandLine().FileNames() ?? [])
        {
            global::Ts.Ast.SourceFile? file;
            var redirect = program.GetParseFileRedirect(fileName);
            if (redirect.Length > 0)
            {
                file = program.GetSourceFile(redirect);
            }
            else
            {
                file = program.GetSourceFile(fileName);
            }
            if (file != null)
            {
                roots[file] = TsPathApi.ToPath(fileName, comparePathsOptions.CurrentDirectory, comparePathsOptions.UseCaseSensitiveFileNames);
            }
        }
    }

    // snapshottobuildinfo.go:214-248
    internal void SetFileInfoAndEmitSignatures()
    {
        List<BuildInfoEmitSignature>? emitSignatures = null;
        buildInfo.FileInfos = program.SourceFiles().Select(file =>
        {
            snapshot.fileInfos.TryLoad(file.Path(), out var info);
            var fileId = ToFileId(file.Path());
            //  tryAddRoot(key, fileId);
            if (buildInfo.FileNames![fileId - 1] != RelativeToBuildInfo(file.Path().ToString()))
            {
                var libFile = program.GetDefaultLibFile(file.Path());
                if (libFile == null || libFile.Replaced || buildInfo.FileNames[fileId - 1] != libFile.Name)
                {
                    throw new InvalidOperationException(
                        $"File name at index {fileId - 1} does not match expected relative path or libName: {buildInfo.FileNames[fileId - 1]} != {RelativeToBuildInfo(file.Path().ToString())}");
                }
            }
            if (snapshot.options.Composite.IsTrue())
            {
                if (!global::Ts.Ast.AstUtilities.IsJsonSourceFile(file) && program.SourceFileMayBeEmitted(file, false))
                {
                    if (!snapshot.emitSignatures.TryLoad(file.Path(), out var emitSignature))
                    {
                        emitSignatures ??= new List<BuildInfoEmitSignature>();
                        emitSignatures.Add(new BuildInfoEmitSignature
                        {
                            FileId = fileId,
                        });
                        buildInfo.EmitSignatures = emitSignatures.ToArray();
                    }
                    else if (emitSignature!.signature != info!.Signature())
                    {
                        var incrementalEmitSignature = new BuildInfoEmitSignature
                        {
                            FileId = fileId,
                        };
                        if (emitSignature.signature.Length > 0)
                        {
                            incrementalEmitSignature.Signature = emitSignature.signature;
                        }
                        else if (emitSignature.signatureWithDifferentOptions![0] == info.Signature())
                        {
                            incrementalEmitSignature.DiffersOnlyInDtsMap = true;
                        }
                        else
                        {
                            incrementalEmitSignature.Signature = emitSignature.signatureWithDifferentOptions[0];
                            incrementalEmitSignature.DiffersInOptions = true;
                        }
                        emitSignatures ??= new List<BuildInfoEmitSignature>();
                        emitSignatures.Add(incrementalEmitSignature);
                        buildInfo.EmitSignatures = emitSignatures.ToArray();
                    }
                }
            }
            return BuildInfoFileInfo.NewBuildInfoFileInfo(info!);
        }).ToArray();
    }

    // snapshottobuildinfo.go:250-280
    internal void SetRootOfIncrementalProgram()
    {
        var keys = roots.Keys.ToList();
        keys.Sort((a, b) => ToFileId(a.Path()) - ToFileId(b.Path()));
        List<BuildInfoRoot>? rootList = null;
        List<BuildInfoResolvedRoot>? resolvedRoots = null;
        foreach (var file in keys)
        {
            var root = ToFileId(roots[file]);
            var resolved = ToFileId(file.Path());
            if (rootList == null)
            {
                // First fileId as is
                rootList = new List<BuildInfoRoot> { new BuildInfoRoot { Start = resolved } };
            }
            else
            {
                var last = rootList[^1];
                if (last.End == resolved - 1)
                {
                    // If its [..., last = [start, end = fileId - 1]], update last to [start, fileId]
                    last.End = resolved;
                }
                else if (last.End == 0 && last.Start == resolved - 1)
                {
                    // If its [..., last = start = fileId - 1 ], update last to [start, fileId]
                    last.End = resolved;
                }
                else
                {
                    rootList.Add(new BuildInfoRoot { Start = resolved });
                }
            }
            buildInfo.Root = rootList.ToArray();
            if (root != resolved)
            {
                resolvedRoots ??= new List<BuildInfoResolvedRoot>();
                resolvedRoots.Add(new BuildInfoResolvedRoot
                {
                    Resolved = resolved,
                    Root = root,
                });
                buildInfo.ResolvedRoot = resolvedRoots.ToArray();
            }
        }
    }

    // snapshottobuildinfo.go:282-300
    internal void SetCompilerOptions()
    {
        global::Ts.TsOptions.DeclsCompiler.ForEachCompilerOptionValue(
            snapshot.options,
            option => option.AffectsBuildInfo,
            (option, value, i) =>
            {
                if (IsZeroOptionValue(value))
                {
                    return false;
                }
                // Make it relative to buildInfo directory if file path
                buildInfo.Options ??= new OrderedMap<string, object?>();
                buildInfo.Options.Set(option.Name, ToRelativeToBuildInfoCompilerOptionValue(option, value));
                return false;
            });
    }

    // reflect.Value.IsZero over the boxed field value (Go fields: Tristate/string/[]string/*int/
    // int-enums/*OrderedMap — nil pointers and nil slices box to null here).
    private static bool IsZeroOptionValue(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        bool b => !b,
        Enum en => ((IConvertible)en).ToInt64(CultureInfo.InvariantCulture) == 0,
        int i => i == 0,
        _ => false,
    };

    // snapshottobuildinfo.go:302-312
    internal void SetReferencedMap()
    {
        var keys = snapshot.referencedMap.GetPathsWithReferences();
        if (keys.Length == 0)
        {
            // Go: slices.Collect over an empty map yields nil → core.Map(nil) → nil (omitted).
            return;
        }
        Array.Sort(keys);
        buildInfo.ReferencedMap = keys.Select(filePath =>
        {
            snapshot.referencedMap.TryGetReferences(filePath, out var references);
            return new BuildInfoReferenceMapEntry
            {
                FileId = ToFileId(filePath),
                FileIdListId = ToFileIdListId(references!),
            };
        }).ToArray();
    }

    // snapshottobuildinfo.go:314-318
    internal void SetChangeFileSet()
    {
        var files = snapshot.changedFilesSet.Keys().ToArray();
        Array.Sort(files);
        if (files.Length > 0)
        {
            buildInfo.ChangeFileSet = files.Select(ToFileId).ToArray();
        }
    }

    // snapshottobuildinfo.go:320-338
    internal void SetSemanticDiagnostics()
    {
        List<BuildInfoSemanticDiagnostic>? semanticDiagnostics = null;
        foreach (var file in program.SourceFiles())
        {
            if (!snapshot.semanticDiagnosticsPerFile.TryLoad(file.Path(), out var value))
            {
                if (!snapshot.changedFilesSet.Has(file.Path()))
                {
                    semanticDiagnostics ??= new List<BuildInfoSemanticDiagnostic>();
                    semanticDiagnostics.Add(new BuildInfoSemanticDiagnostic
                    {
                        FileId = ToFileId(file.Path()),
                    });
                    buildInfo.SemanticDiagnosticsPerFile = semanticDiagnostics.ToArray();
                }
            }
            else
            {
                var diagnostics = ToBuildInfoDiagnosticsOfFile(file.Path(), value!);
                if (diagnostics != null)
                {
                    semanticDiagnostics ??= new List<BuildInfoSemanticDiagnostic>();
                    semanticDiagnostics.Add(new BuildInfoSemanticDiagnostic
                    {
                        Diagnostics = diagnostics,
                    });
                    buildInfo.SemanticDiagnosticsPerFile = semanticDiagnostics.ToArray();
                }
            }
        }
    }

    // snapshottobuildinfo.go:340-347
    internal void SetEmitDiagnostics()
    {
        var files = snapshot.emitDiagnosticsPerFile.Keys().ToArray();
        Array.Sort(files);
        if (files.Length > 0)
        {
            buildInfo.EmitDiagnosticsPerFile = files.Select(filePath =>
            {
                snapshot.emitDiagnosticsPerFile.TryLoad(filePath, out var value);
                return ToBuildInfoDiagnosticsOfFile(filePath, value!);
            }).Where(d => d != null).Select(d => d!).ToArray();
        }
    }

    // snapshottobuildinfo.go:349-364
    internal void SetAffectedFilesPendingEmit()
    {
        var files = snapshot.affectedFilesPendingEmit.Keys().ToArray();
        Array.Sort(files);
        var fullEmitKind = FileEmitKindHelpers.GetFileEmitKind(snapshot.options);
        List<BuildInfoFilePendingEmit>? pendingEmits = null;
        foreach (var filePath in files)
        {
            var file = program.GetSourceFileByPath(filePath);
            if (file == null || !program.SourceFileMayBeEmitted(file, false))
            {
                continue;
            }
            snapshot.affectedFilesPendingEmit.TryLoad(filePath, out var pendingEmit);
            pendingEmits ??= new List<BuildInfoFilePendingEmit>();
            pendingEmits.Add(new BuildInfoFilePendingEmit
            {
                FileId = ToFileId(filePath),
                EmitKind = pendingEmit == fullEmitKind ? 0 : pendingEmit,
            });
            buildInfo.AffectedFilesPendingEmit = pendingEmits.ToArray();
        }
    }

    // snapshottobuildinfo.go:366-372
    internal void SetRootOfNonIncrementalProgram()
    {
        buildInfo.Root = (program.CommandLine().FileNames() ?? []).Select(fileName => new BuildInfoRoot
        {
            NonIncremental = RelativeToBuildInfo(TsPathApi.ToPath(fileName, comparePathsOptions.CurrentDirectory, comparePathsOptions.UseCaseSensitiveFileNames).ToString()),
        }).ToArray();
    }

    // snapshottobuildinfo.go:374-381
    internal void SetPackageJsons()
    {
        if (snapshot.packageJsons is { Length: > 0 })
        {
            buildInfo.PackageJsons = snapshot.packageJsons.Select(RelativeToBuildInfo).ToArray();
        }
        if (snapshot.missingPackageJsons is { Length: > 0 })
        {
            buildInfo.MissingPackageJsons = snapshot.missingPackageJsons.Select(RelativeToBuildInfo).ToArray();
        }
    }
}
