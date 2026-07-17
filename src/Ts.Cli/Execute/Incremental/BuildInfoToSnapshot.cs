// Ported from tsgo internal/execute/incremental/buildinfotosnapshot.go.

using System;
using System.Linq;
using Ts.Collections;
using Ts.Core;

namespace Ts.Execute.Incremental;

using PathT = global::Ts.TsPath.Path;
using TsPathApi = global::Ts.TsPath.TsPath;

internal static class BuildInfoToSnapshotConverter
{
    // buildinfotosnapshot.go:12-47
    internal static Snapshot BuildInfoToSnapshot(BuildInfo buildInfo, global::Ts.TsOptions.ParsedCommandLine config, global::Ts.Compiler.CompilerHost host)
    {
        var to = new ToSnapshot
        {
            buildInfo = buildInfo,
            buildInfoDirectory = TsPathApi.GetDirectoryPath(TsPathApi.GetNormalizedAbsolutePath(config.GetBuildInfoFileName(), config.GetCurrentDirectory())),
        };
        to.filePaths = (buildInfo.FileNames ?? []).Select(fileName =>
        {
            if (BuildInfoHelpers.IsBuildInfoFileNameDefaultLibrary(fileName))
            {
                return TsPathApi.ToPath(TsPathApi.CombinePaths(host.DefaultLibraryPath(), fileName), host.GetCurrentDirectory(), host.FS().UseCaseSensitiveFileNames());
            }
            return TsPathApi.ToPath(fileName, to.buildInfoDirectory, config.UseCaseSensitiveFileNames());
        }).ToArray();
        to.filePathSet = (buildInfo.FileIdsList ?? []).Select(fileIdList =>
        {
            var fileSet = new Set<PathT>(fileIdList.Length);
            foreach (var fileId in fileIdList)
            {
                fileSet.Add(to.ToFilePath(fileId));
            }
            return fileSet;
        }).ToArray();
        to.SetCompilerOptions();
        to.SetFileInfoAndEmitSignatures();
        to.SetReferencedMap();
        to.SetChangeFileSet();
        to.SetSemanticDiagnostics();
        to.SetEmitDiagnostics();
        to.SetAffectedFilesPendingEmit();
        if (buildInfo.LatestChangedDtsFile.Length > 0)
        {
            to.snapshot.latestChangedDtsFile = to.ToAbsolutePath(buildInfo.LatestChangedDtsFile);
        }
        to.snapshot.hasErrors = buildInfo.Errors ? Tristate.True : Tristate.False;
        to.snapshot.hasSemanticErrors = buildInfo.SemanticErrors;
        to.snapshot.checkPending = buildInfo.CheckPending;
        to.SetPackageJsons();
        return to.snapshot;
    }
}

// buildinfotosnapshot.go:49-55
internal sealed class ToSnapshot
{
    internal BuildInfo buildInfo = null!;
    internal string buildInfoDirectory = "";
    internal readonly Snapshot snapshot = new();
    internal PathT[] filePaths = [];
    internal Set<PathT>[] filePathSet = [];

    // buildinfotosnapshot.go:57-59
    internal string ToAbsolutePath(string path) => TsPathApi.GetNormalizedAbsolutePath(path, buildInfoDirectory);

    // buildinfotosnapshot.go:61-63
    internal PathT ToFilePath(int fileId) => filePaths[fileId - 1];

    // buildinfotosnapshot.go:65-67
    internal Set<PathT> ToFilePathSet(int fileIdListId) => filePathSet[fileIdListId - 1];

    // buildinfotosnapshot.go:69-92
    internal BuildInfoDiagnosticWithFileName[]? ToBuildInfoDiagnosticsWithFileName(BuildInfoDiagnostic[]? diagnostics)
    {
        if (diagnostics == null)
        {
            return null;
        }
        return diagnostics.Select(d =>
        {
            var file = default(PathT);
            if (d.File != 0)
            {
                file = ToFilePath(d.File);
            }
            return new BuildInfoDiagnosticWithFileName
            {
                file = file,
                noFile = d.NoFile,
                pos = d.Pos,
                end = d.End,
                code = d.Code,
                category = d.Category,
                messageKey = d.MessageKey,
                messageArgs = d.MessageArgs,
                messageChain = ToBuildInfoDiagnosticsWithFileName(d.MessageChain),
                relatedInformation = ToBuildInfoDiagnosticsWithFileName(d.RelatedInformation),
                reportsUnnecessary = d.ReportsUnnecessary,
                reportsDeprecated = d.ReportsDeprecated,
                skippedOnNoEmit = d.SkippedOnNoEmit,
                repopulateInfo = FromBuildInfoRepopulateInfo(d.RepopulateInfo),
            };
        }).ToArray();
    }

    // buildinfotosnapshot.go:94-98
    internal DiagnosticsOrBuildInfoDiagnosticsWithFileName ToDiagnosticsOrBuildInfoDiagnosticsWithFileName(BuildInfoDiagnosticsOfFile dig)
    {
        return new DiagnosticsOrBuildInfoDiagnosticsWithFileName
        {
            buildInfoDiagnostics = ToBuildInfoDiagnosticsWithFileName(dig.Diagnostics),
        };
    }

    // buildinfotosnapshot.go:100-110
    internal static global::Ts.Ast.RepopulateDiagnosticInfo? FromBuildInfoRepopulateInfo(BuildInfoRepopulateInfo? info)
    {
        if (info == null)
        {
            return null;
        }
        return new global::Ts.Ast.RepopulateDiagnosticInfo
        {
            Kind = info.Kind,
            ModuleReference = info.ModuleReference,
            Mode = info.Mode,
            PackageName = info.PackageName,
        };
    }

    // buildinfotosnapshot.go:112-114
    internal void SetCompilerOptions()
    {
        snapshot.options = buildInfo.GetCompilerOptions(buildInfoDirectory);
    }

    // buildinfotosnapshot.go:116-136
    internal void SetFileInfoAndEmitSignatures()
    {
        var isComposite = snapshot.options.Composite.IsTrue();
        var index = 0;
        foreach (var buildInfoFileInfo in buildInfo.FileInfos ?? [])
        {
            var path = ToFilePath(index + 1);
            var info = buildInfoFileInfo.GetFileInfo();
            snapshot.fileInfos.Store(path, info);
            // Add default emit signature as file's signature
            if (info.Signature().Length > 0 && isComposite)
            {
                snapshot.emitSignatures.Store(path, new EmitSignature { signature = info.Signature() });
            }
            index++;
        }
        // Fix up emit signatures
        foreach (var value in buildInfo.EmitSignatures ?? [])
        {
            if (value.NoEmitSignature())
            {
                snapshot.emitSignatures.Delete(ToFilePath(value.FileId));
            }
            else
            {
                var path = ToFilePath(value.FileId);
                snapshot.emitSignatures.Store(path, value.ToEmitSignature(path.ToString(), _ =>
                {
                    snapshot.emitSignatures.TryLoad(path, out var existing);
                    return existing;
                }));
            }
        }
    }

    // buildinfotosnapshot.go:138-142
    internal void SetReferencedMap()
    {
        foreach (var entry in buildInfo.ReferencedMap ?? [])
        {
            snapshot.referencedMap.StoreReferences(ToFilePath(entry.FileId), ToFilePathSet(entry.FileIdListId));
        }
    }

    // buildinfotosnapshot.go:144-149
    internal void SetChangeFileSet()
    {
        foreach (var fileId in buildInfo.ChangeFileSet ?? [])
        {
            var filePath = ToFilePath(fileId);
            snapshot.changedFilesSet.Add(filePath);
        }
    }

    // buildinfotosnapshot.go:151-168
    internal void SetSemanticDiagnostics()
    {
        snapshot.fileInfos.Range((path, info) =>
        {
            // Initialize to have no diagnostics if its not changed file
            if (!snapshot.changedFilesSet.Has(path))
            {
                snapshot.semanticDiagnosticsPerFile.Store(path, new DiagnosticsOrBuildInfoDiagnosticsWithFileName());
            }
            return true;
        });
        foreach (var diagnostic in buildInfo.SemanticDiagnosticsPerFile ?? [])
        {
            if (diagnostic.FileId != 0)
            {
                var filePath = ToFilePath(diagnostic.FileId);
                snapshot.semanticDiagnosticsPerFile.Delete(filePath); // does not have cached diagnostics
            }
            else
            {
                var filePath = ToFilePath(diagnostic.Diagnostics!.FileId);
                snapshot.semanticDiagnosticsPerFile.Store(filePath, ToDiagnosticsOrBuildInfoDiagnosticsWithFileName(diagnostic.Diagnostics));
            }
        }
    }

    // buildinfotosnapshot.go:170-175
    internal void SetEmitDiagnostics()
    {
        foreach (var diagnostic in buildInfo.EmitDiagnosticsPerFile ?? [])
        {
            var filePath = ToFilePath(diagnostic.FileId);
            snapshot.emitDiagnosticsPerFile.Store(filePath, ToDiagnosticsOrBuildInfoDiagnosticsWithFileName(diagnostic));
        }
    }

    // buildinfotosnapshot.go:177-185
    internal void SetAffectedFilesPendingEmit()
    {
        if ((buildInfo.AffectedFilesPendingEmit ?? []).Length == 0)
        {
            return;
        }
        var ownOptionsEmitKind = FileEmitKindHelpers.GetFileEmitKind(snapshot.options);
        foreach (var pendingEmit in buildInfo.AffectedFilesPendingEmit!)
        {
            snapshot.affectedFilesPendingEmit.Store(ToFilePath(pendingEmit.FileId), pendingEmit.EmitKind == 0 ? ownOptionsEmitKind : pendingEmit.EmitKind);
        }
    }

    // buildinfotosnapshot.go:187-198
    internal void SetPackageJsons()
    {
        if (buildInfo.PackageJsons != null)
        {
            snapshot.packageJsons = buildInfo.PackageJsons.Select(ToAbsolutePath).ToArray();
        }
        else
        {
            snapshot.packageJsons = [];
        }
        if (buildInfo.MissingPackageJsons != null)
        {
            snapshot.missingPackageJsons = buildInfo.MissingPackageJsons.Select(ToAbsolutePath).ToArray();
        }
        else
        {
            snapshot.missingPackageJsons = [];
        }
    }
}
