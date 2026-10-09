// Ported from tsgo internal/project/snapshot.go (whole file). The value types shared
// with overlayfs.go conventions (FileHandle family) lead the file exactly as in Go;
// the fs/registry builders they interoperate with live in SnapshotFs.cs.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Ts.Collections;
using Ts.Project.Logging;
using static global::Ts.LsProto.DocumentUriExtensions;

namespace Ts.Project;

// snapshot.go:31-33 — FileContent.
public interface FileContent
{
    string Content();

    UInt128 Hash();
}

// snapshot.go:35-44 — FileHandle.
public interface FileHandle : FileContent
{
    string FileName();

    int Version();

    bool MatchesDiskText();

    bool IsOverlay();

    global::Ts.Ls.LsConv.LSPLineMap? LSPLineMap();

    global::Ts.SourceMap.ECMALineInfo? ECMALineInfo();

    global::Ts.Core.ScriptKind Kind();
}

// snapshot.go:46-59 — fileBase.
public abstract class FileBase
{
    internal string fileName = "";
    internal string content = "";
    internal UInt128 hash;

    // Go: lineMapOnce/lineInfoOnce sync.Once → double-checked lazy init.
    private readonly object lineMapOnce = new();
    private global::Ts.Ls.LsConv.LSPLineMap? lineMap;
    private readonly object lineInfoOnce = new();
    private global::Ts.SourceMap.ECMALineInfo? lineInfo;

    public string FileName() => fileName;

    public UInt128 Hash() => hash;

    public string Content() => content;

    // snapshot.go:67-73 — LSPLineMap.
    public global::Ts.Ls.LsConv.LSPLineMap? LSPLineMap()
    {
        if (lineMap is null)
        {
            lock (lineMapOnce)
            {
                lineMap ??= global::Ts.Ls.LsConv.LineMap.ComputeLSPLineStarts(content);
            }
        }
        return lineMap;
    }

    // snapshot.go:75-82 — ECMALineInfo.
    public global::Ts.SourceMap.ECMALineInfo? ECMALineInfo()
    {
        if (lineInfo is null)
        {
            lock (lineInfoOnce)
            {
                if (lineInfo is null)
                {
                    int[] lineStarts = global::Ts.Core.CoreUtil.ComputeECMALineStarts(content);
                    lineInfo = global::Ts.SourceMap.ECMALineInfo.CreateECMALineInfo(content, lineStarts);
                }
            }
        }
        return lineInfo;
    }

    // Go hashes with xxh3.HashString128(content); callers that mutate content rehash
    // through this helper (the Go code assigns the field directly).
    internal void SetContentHash(string newContent)
    {
        content = newContent;
        hash = SnapshotUtil.HashString128(newContent);
    }
}

// snapshot.go:61-70 — diskFile. (Implements dirty.Cloneable so it can be a value in
// dirty.SyncMap — Go's diskFile.Clone at overlayfs.go:105 satisfies the constraint.)
public sealed class DiskFile : FileBase, FileHandle, global::Ts.Project.Dirty.Cloneable<DiskFile>
{
    internal bool needsReload;
    internal global::Ts.TsPath.Path realpathPath;

    // snapshot.go:72-80 — newDiskFile.
    public static DiskFile NewDiskFile(string fileName, string content)
    {
        var f = new DiskFile
        {
            fileName = fileName,
            content = content,
            hash = SnapshotUtil.HashString128(content),
        };
        return f;
    }

    // snapshot.go:84-86 — Version.
    public int Version() => 0;

    // snapshot.go:88-90 — MatchesDiskText.
    public bool MatchesDiskText() => !needsReload;

    // snapshot.go:92-94 — IsOverlay.
    public bool IsOverlay() => false;

    // snapshot.go:96-98 — Kind.
    public global::Ts.Core.ScriptKind Kind() => global::Ts.Core.CoreUtil.GetScriptKindFromFileName(fileName);

    // snapshot.go:100-110 — Clone.
    public DiskFile Clone()
    {
        return new DiskFile
        {
            realpathPath = realpathPath,
            fileName = fileName,
            content = content,
            hash = hash,
        };
    }
}

// snapshot.go:113-136 — Overlay.
public sealed class Overlay : FileBase, FileHandle, global::Ts.Ls.LsConv.Script
{
    internal int version;
    internal global::Ts.Core.ScriptKind kind;
    internal bool matchesDiskText;

    // snapshot.go:119-129 — newOverlay.
    public static Overlay NewOverlay(string fileName, string content, int version, global::Ts.Core.ScriptKind kind)
    {
        var o = new Overlay
        {
            fileName = fileName,
            content = content,
            hash = SnapshotUtil.HashString128(content),
            version = version,
            kind = kind,
        };
        return o;
    }

    // snapshot.go:131-133 — Version.
    public int Version() => version;

    // snapshot.go:135-137 — Text.
    public string Text() => content;

    // MatchesDiskText may return false negatives, but never false positives.
    public bool MatchesDiskText() => matchesDiskText;

    // !!! optimization: incorporate mtime
    // snapshot.go:143-152 — computeMatchesDiskText.
    public (bool MatchesDiskText, bool Exists) ComputeMatchesDiskText(global::Ts.Vfs.IFileSystem fs)
    {
        if (global::Ts.TsPath.TsPath.IsDynamicFileName(fileName))
        {
            return (false, false);
        }
        if (!fs.TryReadFile(fileName, out string diskContent))
        {
            return (false, false);
        }
        return (SnapshotUtil.HashString128(diskContent) == hash, true);
    }

    // snapshot.go:154-156 — IsOverlay.
    public bool IsOverlay() => true;

    // snapshot.go:158-160 — Kind.
    public global::Ts.Core.ScriptKind Kind() => kind;

    // Direct field writes from overlayfs.go's processChanges (same package in Go).
    internal void SetVersion(int value) => version = value;

    internal void SetMatchesDiskText(bool value) => matchesDiskText = value;

    internal void Rehash() => hash = SnapshotUtil.HashString128(content);
}

// snapshot.go:162-186 — Snapshot.
public sealed class Snapshot
{
    private readonly ulong id;
    private ulong parentId;
    private int refCount; // Go: atomic.Int32

    // Cross-type accessors (Go: same-package field access from session.go logging).
    internal ulong SnapshotId => id;

    internal ulong SnapshotParentId => parentId;

    // Session options are immutable for the server lifetime,
    // so can be a pointer.
    private readonly SessionOptions sessionOptions;
    private readonly Func<string, global::Ts.TsPath.Path> toPath;
    private global::Ts.Ls.LsConv.Converters converters = null!;

    // Immutable state, cloned between snapshots.
    internal SnapshotFS fs = null!;
    public ProjectCollection ProjectCollection { get; internal set; } = null!;

    public ConfigFileRegistry ConfigFileRegistry { get; internal set; } = null!;

    public global::Ts.Ls.AutoImport.Registry? AutoImports { get; internal set; }

    internal WatchedFiles<Dictionary<global::Ts.TsPath.Path, string>>? autoImportsWatch;

    private readonly global::Ts.Core.CompilerOptions? compilerOptionsForInferredProjects;
    private readonly global::Ts.Ls.UserPreferences userPreferences;

    internal global::Ts.Project.Logging.LogTree? builderLogs { get; set; }

    internal Exception? apiError { get; set; }

    private Snapshot(
        ulong id,
        SessionOptions sessionOptions,
        Func<string, global::Ts.TsPath.Path> toPath,
        global::Ts.Core.CompilerOptions? compilerOptionsForInferredProjects,
        global::Ts.Ls.UserPreferences userPreferences)
    {
        this.id = id;
        this.sessionOptions = sessionOptions;
        this.toPath = toPath;
        this.compilerOptionsForInferredProjects = compilerOptionsForInferredProjects;
        this.userPreferences = userPreferences;
    }

    // snapshot.go:188-217 — NewSnapshot initializes a snapshot with refCount 1.
    // The caller is responsible for calling Deref when done.
    public static Snapshot NewSnapshot(
        ulong id,
        SnapshotFS fs,
        SessionOptions sessionOptions,
        ConfigFileRegistry? configFileRegistry,
        global::Ts.Core.CompilerOptions? compilerOptionsForInferredProjects,
        global::Ts.Ls.UserPreferences userPreferences,
        global::Ts.Ls.AutoImport.Registry? autoImports,
        WatchedFiles<Dictionary<global::Ts.TsPath.Path, string>>? autoImportsWatch,
        Func<string, global::Ts.TsPath.Path> toPath)
    {
        var s = new Snapshot(id, sessionOptions, toPath, compilerOptionsForInferredProjects, userPreferences)
        {
            fs = fs,
            ConfigFileRegistry = configFileRegistry ?? new ConfigFileRegistry(),
            ProjectCollection = ProjectCollection.NewProjectCollection(toPath, SnapshotUtil.OpenFilePaths(fs.Overlays)),
            AutoImports = autoImports,
            autoImportsWatch = autoImportsWatch,
        };
        Interlocked.Exchange(ref s.refCount, 1);
        s.converters = new global::Ts.Ls.LsConv.Converters(s.sessionOptions.PositionEncoding.Value ?? "", fileName => s.LSPLineMap(fileName)!);
        return s;
    }

    // snapshot.go:219-221 — GetDefaultProject.
    public Project? GetDefaultProject(global::Ts.LsProto.DocumentUri uri) =>
        ProjectCollection.GetDefaultProject(uri.Path(UseCaseSensitiveFileNames()));

    // snapshot.go:223-228 — GetProjectsContainingFile.
    public global::Ts.Ls.Project[] GetProjectsContainingFile(global::Ts.LsProto.DocumentUri uri)
    {
        string fileName = uri.FileName();
        global::Ts.TsPath.Path path = toPath(fileName);
        // TODO!! sheetal may be change this to handle symlinks!!
        return ProjectCollection.GetProjectsContainingFile(path).ToArray();
    }

    // snapshot.go:230-232 — GetFile.
    public FileHandle? GetFile(string fileName) => fs.GetFile(fileName);

    // snapshot.go:234-239 — LSPLineMap.
    public global::Ts.Ls.LsConv.LSPLineMap? LSPLineMap(string fileName)
    {
        FileHandle? file = fs.GetFile(fileName);
        if (file is not null)
        {
            return file.LSPLineMap();
        }
        return null;
    }

    // snapshot.go:241-246 — GetECMALineInfo.
    public global::Ts.SourceMap.ECMALineInfo? GetECMALineInfo(string fileName)
    {
        FileHandle? file = fs.GetFile(fileName);
        if (file is not null)
        {
            return file.ECMALineInfo();
        }
        return null;
    }

    // snapshot.go:248-250 — GetPreferences.
    public global::Ts.Ls.UserPreferences GetPreferences(string activeFile) => userPreferences;

    // snapshot.go:252-254 — UserPreferences.
    public global::Ts.Ls.UserPreferences UserPreferences() => userPreferences;

    // snapshot.go:256-258 — Converters.
    public global::Ts.Ls.LsConv.Converters Converters() => converters;

    // snapshot.go:260-262 — AutoImportRegistry.
    public global::Ts.Ls.AutoImport.Registry? AutoImportRegistry() => AutoImports;

    // snapshot.go:264-266 — ID.
    public ulong ID() => id;

    // snapshot.go:268-270 — UseCaseSensitiveFileNames.
    public bool UseCaseSensitiveFileNames() => fs.fs.UseCaseSensitiveFileNames();

    // snapshot.go:272-278 — ReadFile.
    public (string Content, bool Ok) ReadFile(string fileName)
    {
        FileHandle? handle = GetFile(fileName);
        if (handle is null)
        {
            return ("", false);
        }
        return (handle.Content(), true);
    }

    // snapshot.go:280-282 — DirectoryExists.
    public bool DirectoryExists(string path) => fs.fs.DirectoryExists(path);

    // snapshot.go:284-286 — FileExists.
    public bool FileExists(string path) => fs.fs.FileExists(path);

    // snapshot.go:288-290 — GetDirectories.
    public string[] GetDirectories(string path) => fs.fs.GetAccessibleEntries(path).Directories ?? [];

    // snapshot.go:292-294 — ReadDirectory.
    public string[] ReadDirectory(string currentDir, string path, string[]? extensions, string[]? excludes, string[]? includes, int depth) =>
        global::Ts.Vfs.VfsMatch.VfsMatch.ReadDirectory(fs.fs, currentDir, path, extensions, excludes, includes, depth).ToArray();

    // Go: the *Snapshot passed as the ls.Host argument of ls.NewLanguageService.
    internal SessionOptions Options() => sessionOptions;

    // PORT-NOTE: Go's ls.Host is the snapshot itself, with the program passed alongside;
    // the C# Ts.Ls.Host also owns the program/checker lifetime surface, so the snapshot is
    // adapted together with the project's program.
    internal global::Ts.Ls.Host LsHost(global::Ts.Compiler.Program program) => new SnapshotLsHost(this, program);

    private sealed class SnapshotLsHost : global::Ts.Ls.Host
    {
        private readonly Snapshot snapshot;
        private readonly global::Ts.Compiler.Program program;

        public SnapshotLsHost(Snapshot snapshot, global::Ts.Compiler.Program program)
        {
            this.snapshot = snapshot;
            this.program = program;
        }

        public global::Ts.Compiler.Program GetProgram() => program;

        public (global::Ts.Checker.Checker Checker, Action Release) GetTypeCheckerForFile(
            System.Threading.CancellationToken cancellationToken,
            global::Ts.Ast.SourceFile file) => program.GetTypeCheckerForFile(cancellationToken, file);

        public string[] ReadDirectory(string currentDir, string path, string[]? extensions, string[]? excludes, string[]? includes, int depth) =>
            snapshot.ReadDirectory(currentDir, path, extensions, excludes, includes, depth);

        public string[] GetDirectories(string path) => snapshot.GetDirectories(path);

        public bool DirectoryExists(string path) => snapshot.DirectoryExists(path);

        public bool FileExists(string path) => snapshot.FileExists(path);

        public global::Ts.Ls.AutoImport.Registry? AutoImportRegistry() => snapshot.AutoImportRegistry();

        public global::Ts.Ls.LsConv.Converters? Converters() => snapshot.Converters();
    }

    // snapshot.go:297-303 — APISnapshotRequest.
    public sealed class APISnapshotRequest
    {
        public global::Ts.Collections.Set<string>? OpenProjects { get; init; }

        public global::Ts.Collections.Set<global::Ts.TsPath.Path>? CloseProjects { get; init; }

        public global::Ts.Collections.Set<global::Ts.LsProto.DocumentUri>? OpenFiles { get; init; }

        public global::Ts.Collections.Set<global::Ts.TsPath.Path>? CloseFiles { get; init; }
    }

    // snapshot.go:305-308 — ProjectTreeRequest.
    public sealed class ProjectTreeRequest
    {
        // If null, all project trees need to be loaded, otherwise only those that are referenced.
        public global::Ts.Collections.Set<global::Ts.TsPath.Path>? referencedProjects;

        public ProjectTreeRequest(global::Ts.Collections.Set<global::Ts.TsPath.Path>? referencedProjects)
        {
            this.referencedProjects = referencedProjects;
        }

        // snapshot.go:310-312 — IsAllProjects.
        public bool IsAllProjects() => referencedProjects is null;

        // snapshot.go:314-316 — IsProjectReferenced.
        public bool IsProjectReferenced(global::Ts.TsPath.Path projectID) => referencedProjects.Has(projectID);

        // snapshot.go:318-324 — Projects.
        public global::Ts.TsPath.Path[] Projects()
        {
            if (referencedProjects is null)
            {
                return [];
            }
            // map-iter: 已排序 — Go's map-key iteration here surfaces as Keys(); consumers
            // only test membership/containment on the result.
            var result = new List<global::Ts.TsPath.Path>();
            foreach (var p in referencedProjects.Keys())
            {
                result.Add(p);
            }
            return result.ToArray();
        }
    }

    // snapshot.go:326-341 — ResourceRequest.
    public sealed class ResourceRequest
    {
        // Documents are URIs that were requested by the client.
        // The new snapshot should ensure projects for these URIs have loaded programs.
        public global::Ts.LsProto.DocumentUri[] Documents { get; init; } = [];

        // ConfiguredProjectDocuments are URIs for which configured projects should be loaded
        // (if disableSolutionSearching/disableReferencedProjectLoad settings allow),
        // but no inferred project should be created if no configured project is found.
        // This is used by cross-project operations like find-all-references.
        public global::Ts.LsProto.DocumentUri[] ConfiguredProjectDocuments { get; init; } = [];

        // Update requested Projects.
        // this is used when we want to get LS and from all the Projects the file can be part of.
        public global::Ts.TsPath.Path[] Projects { get; init; } = [];

        // Update and ensure project trees that reference the projects.
        // This is used to compute the solution and project tree so that
        // we can find references across all the projects in the solution irrespective of which project is open.
        public ProjectTreeRequest? ProjectTree { get; init; }

        // AutoImports is the document URI for which auto imports should be prepared.
        public global::Ts.LsProto.DocumentUri AutoImports { get; init; }
    }

    // snapshot.go:343-358 — SnapshotChange. (PORT-NOTE: Go embeds resourceRequest with
    // promoted fields; ResourceRequest is sealed here, so this is composition + promoted
    // accessors — read sites keep Go's `change.Documents` field shape, and Session
    // constructors assign the embedded request in one place.)
    public sealed class SnapshotChange
    {
        // Go: embedded resourceRequest.
        internal ResourceRequest ResourceRequest { get; init; } = new();

        // Promoted fields of the embedded resourceRequest.
        public global::Ts.LsProto.DocumentUri[] Documents => ResourceRequest.Documents;

        public global::Ts.LsProto.DocumentUri[] ConfiguredProjectDocuments => ResourceRequest.ConfiguredProjectDocuments;

        public global::Ts.TsPath.Path[] Projects => ResourceRequest.Projects;

        public ProjectTreeRequest? ProjectTree => ResourceRequest.ProjectTree;

        public global::Ts.LsProto.DocumentUri AutoImports => ResourceRequest.AutoImports;

        internal UpdateReason Reason { get; init; }

        // fileChanges are the changes that have occurred since the last snapshot.
        internal FileChangeSummary FileChanges { get; set; } = new();

        // compilerOptionsForInferredProjects is the compiler options to use for inferred projects.
        // It should only be set the value in the next snapshot should be changed. If nil, the
        // value from the previous snapshot will be copied to the new snapshot.
        internal global::Ts.Core.CompilerOptions? CompilerOptionsForInferredProjects { get; init; }

        internal global::Ts.Ls.UserPreferences? NewConfig { get; init; }

        // ataChanges contains ATA-related changes to apply to projects in the new snapshot.
        internal Dictionary<global::Ts.TsPath.Path, ATAStateChange> AtaChanges { get; init; } = [];

        internal APISnapshotRequest? ApiRequest { get; init; }

        // cleanDiskCache triggers cleaning of cached disk files not referenced by any open project.
        internal bool CleanDiskCache { get; init; }
    }

    // snapshot.go:361-370 — ATAStateChange represents a change to a project's ATA state.
    public sealed class ATAStateChange
    {
        public global::Ts.TsPath.Path ProjectID { get; init; }

        // TypingsInfo is the new typings info for the project.
        public global::Ts.Project.Ata.TypingsInfo? TypingsInfo { get; init; }

        // TypingsFiles is the new list of typing files for the project.
        public string[] TypingsFiles { get; init; } = [];

        // TypingsFilesToWatch is the new list of typing files to watch for changes.
        public string[] TypingsFilesToWatch { get; init; } = [];

        public global::Ts.Project.Logging.LogTree? Logs { get; init; }
    }

    // snapshot.go:372-556 — Clone.
    public Snapshot Clone(
        global::Ts.Core.RequestContext ctx,
        SnapshotChange change,
        Dictionary<global::Ts.TsPath.Path, Overlay> overlays,
        Session session)
    {
        global::Ts.Project.Logging.LogTree? logger = null;
        var start = Stopwatch.StartNew();

        // Print in-progress logs immediately if cloning fails (Go: defer + recover).
        if (session.options.LoggingEnabled)
        {
            try
            {
                return CloneWorker(ctx, change, overlays, session, ref logger, start);
            }
            catch (Exception)
            {
                session.logger.Log(logger!.ToString());
                throw;
            }
        }
        return CloneWorker(ctx, change, overlays, session, ref logger, start);
    }

    private Snapshot CloneWorker(
        global::Ts.Core.RequestContext ctx,
        SnapshotChange change,
        Dictionary<global::Ts.TsPath.Path, Overlay> overlays,
        Session session,
        ref global::Ts.Project.Logging.LogTree? logger,
        Stopwatch start)
    {
        if (session.options.LoggingEnabled)
        {
            logger = new global::Ts.Project.Logging.LogTree($"Cloning snapshot {id}");
            string GetDetails()
            {
                var details = "";
                if (change.Documents.Length != 0)
                {
                    details += $" Documents: {SnapshotUtil.FormatDocuments(change.Documents)}";
                }
                if (change.ConfiguredProjectDocuments.Length != 0)
                {
                    details += $" ConfiguredProjectDocuments: {SnapshotUtil.FormatDocuments(change.ConfiguredProjectDocuments)}";
                }
                if (change.Projects.Length != 0)
                {
                    details += $" Projects: {SnapshotUtil.FormatPaths(change.Projects)}";
                }
                if (change.ProjectTree is not null)
                {
                    details += $" ProjectTree: {SnapshotUtil.FormatPaths(change.ProjectTree.Projects())}";
                }
                return details;
            }
            switch (change.Reason)
            {
                case UpdateReason.DidOpenFile:
                    logger.Logf("Reason: DidOpenFile - {0}", change.FileChanges.Opened.Value);
                    break;
                case UpdateReason.DidCloseFile:
                    logger.Logf("Reason: DidCloseFile - {0}", SnapshotUtil.FormatDocuments(change.FileChanges.Closed));
                    break;
                case UpdateReason.DidChangeCompilerOptionsForInferredProjects:
                    logger.Logf("Reason: DidChangeCompilerOptionsForInferredProjects");
                    break;
                case UpdateReason.RequestedLanguageServicePendingChanges:
                    logger.Logf("Reason: RequestedLanguageService (pending file changes) - {0}", GetDetails());
                    break;
                case UpdateReason.RequestedLanguageServiceProjectNotLoaded:
                    logger.Logf("Reason: RequestedLanguageService (project not loaded) - {0}", GetDetails());
                    break;
                case UpdateReason.RequestedLanguageServiceForFileNotOpen:
                    logger.Logf("Reason: RequestedLanguageService (file not open) - {0}", GetDetails());
                    break;
                case UpdateReason.RequestedLanguageServiceProjectDirty:
                    logger.Logf("Reason: RequestedLanguageService (project dirty) - {0}", GetDetails());
                    break;
                case UpdateReason.RequestedLoadProjectTree:
                    logger.Logf("Reason: RequestedLoadProjectTree - {0}", GetDetails());
                    break;
                case UpdateReason.IdleCleanDiskCache:
                    logger.Logf("Reason: IdleCleanDiskCache");
                    break;
            }
        }

        SnapshotFSBuilder fsBuilder = SnapshotFSBuilder.NewSnapshotFSBuilder(
            session.fs.fs,
            fs.Overlays,
            overlays,
            fs.DiskFiles ?? [],
            fs.DiskDirectories ?? [],
            fs.NodeModulesRealpathAliases ?? [],
            session.options.PositionEncoding.Value ?? "",
            toPath);
        if (change.FileChanges.HasExcessiveWatchEvents())
        {
            var invalidateStart = Stopwatch.StartNew();
            if (change.FileChanges.InvalidateAll)
            {
                fsBuilder.InvalidateCache();
                logger.Logf("InvalidateAll: invalidated file cache in {0}", invalidateStart.Elapsed);
            }
            else if (!fsBuilder.WatchChangesOverlapCache(change.FileChanges))
            {
                // All watch changes/deletes are files we haven't seen; should be irrelevant to us (probably an external tool's build or something)
                change.FileChanges.Changed = new global::Ts.Collections.Set<global::Ts.LsProto.DocumentUri>();
                change.FileChanges.Deleted = new global::Ts.Collections.Set<global::Ts.LsProto.DocumentUri>();
            }
            else if (change.FileChanges.IncludesWatchChangeOutsideNodeModules)
            {
                fsBuilder.InvalidateCache();
                logger.Logf("Excessive watch changes detected, invalidated file cache in {0}", invalidateStart.Elapsed);
            }
            else
            {
                fsBuilder.InvalidateNodeModulesCache();
                logger.Logf("npm install detected, invalidated node_modules cache in {0}", invalidateStart.Elapsed);
            }
        }
        else
        {
            change.FileChanges = fsBuilder.ExpandAndFilterWatchEvents(change.FileChanges);
            change.FileChanges = fs.ExpandRealpathAliases(change.FileChanges);
            fsBuilder.MarkDirtyFiles(change.FileChanges);
            change.FileChanges = fsBuilder.ConvertOpenAndCloseToChanges(change.FileChanges);
        }

        global::Ts.Core.CompilerOptions? compilerOptionsForInferredProjects = this.compilerOptionsForInferredProjects;
        if (change.CompilerOptionsForInferredProjects is not null)
        {
            // !!! mark inferred projects as dirty?
            compilerOptionsForInferredProjects = change.CompilerOptionsForInferredProjects;
        }

        // Compute effective customConfigFileName from user preferences
        string customConfigFileName = ConfigFileRegistry.customConfigFileName;
        if (change.NewConfig is not null)
        {
            customConfigFileName = change.NewConfig.CustomConfigFileName;
        }

        ulong newSnapshotID = session.IncrementSnapshotID();
        ProjectCollectionBuilder projectCollectionBuilder = ProjectCollectionBuilder.NewProjectCollectionBuilder(
            ctx,
            newSnapshotID,
            fsBuilder,
            ProjectCollection,
            ConfigFileRegistry,
            ProjectCollection.ApiState,
            compilerOptionsForInferredProjects,
            sessionOptions,
            customConfigFileName,
            session.parseCache,
            session.extendedConfigCache,
            session.client);

        if (change.AtaChanges.Count != 0)
        {
            projectCollectionBuilder.DidUpdateATAState(change.AtaChanges, logger!.Fork("DidUpdateATAState"));
        }

        projectCollectionBuilder.DidChangeCustomConfigFileName(logger!.Fork("DidChangeCustomConfigFileName"));

        if (!change.FileChanges.IsEmpty())
        {
            projectCollectionBuilder.DidChangeFiles(change.FileChanges, logger.Fork("DidChangeFiles"));
        }

        Exception? apiError = null;
        if (change.ApiRequest is not null)
        {
            apiError = projectCollectionBuilder.HandleAPIRequest(change.ApiRequest, logger.Fork("HandleAPIRequest"));
        }

        foreach (var uri in change.Documents)
        {
            projectCollectionBuilder.DidRequestFile(uri, false /*configuredProjectsOnly*/, logger.Fork("DidRequestFile"));
        }

        foreach (var uri in change.ConfiguredProjectDocuments)
        {
            projectCollectionBuilder.DidRequestFile(uri, true /*configuredProjectsOnly*/, logger.Fork("DidRequestFile (optional)"));
        }

        foreach (var projectId in change.Projects)
        {
            projectCollectionBuilder.DidRequestProject(projectId, logger.Fork("DidRequestProject"));
        }

        if (change.ProjectTree is not null)
        {
            projectCollectionBuilder.DidRequestProjectTrees(change.ProjectTree, logger.Fork("DidRequestProjectTrees"));
        }

        (ProjectCollection projectCollection, ConfigFileRegistry configFileRegistry) = projectCollectionBuilder.Finalize(logger);

        var projectsWithNewProgramStructure = new Dictionary<global::Ts.TsPath.Path, bool>();
        // map-iter: 序无关 — projectsWithNewProgramStructure is keyed per project.
        foreach (Project project in projectCollection.Projects())
        {
            if (project.ProgramLastUpdate == newSnapshotID && project.ProgramUpdateKind != ProgramUpdateKind.Cloned)
            {
                projectsWithNewProgramStructure[project.configFilePath] = project.ProgramUpdateKind == ProgramUpdateKind.NewFiles;
            }
        }

        // Clean cached disk files not touched by any open project on file open, close, delete,
        // or when explicitly requested (e.g. by an idle timer).
        bool shouldCleanDiskCache = change.CleanDiskCache ||
            !string.IsNullOrEmpty(change.FileChanges.Opened.Value) ||
            !string.IsNullOrEmpty(change.FileChanges.Reopened.Value) ||
            change.FileChanges.Closed.Len() > 0 ||
            change.FileChanges.Deleted.Len() > 0;
        if (shouldCleanDiskCache)
        {
            // The set of seen files can change only if a program was constructed (not cloned) during this snapshot.
            // When cleanDiskCache is explicitly set, always attempt cleaning.
            if (projectsWithNewProgramStructure.Count > 0 || change.CleanDiskCache)
            {
                var cleanFilesStart = Stopwatch.StartNew();
                int removedFiles = 0;
                fsBuilder.DiskFiles.Range(entry =>
                {
                    // map-iter: 序无关 — the scan short-circuits only per-entry.
                    foreach (Project project in projectCollection.Projects())
                    {
                        if (project.host is not null && project.host.sourceFS.SeenFile(entry.Key()))
                        {
                            return true;
                        }
                    }
                    entry.Delete();
                    removedFiles++;
                    return true;
                });
                if (session.options.LoggingEnabled)
                {
                    logger.Logf("Removed {0} cached file(s) in {1}", removedFiles, cleanFilesStart.Elapsed);
                }
            }
        }

        global::Ts.Ls.UserPreferences config = userPreferences;
        if (change.NewConfig is not null)
        {
            config = change.NewConfig;
        }

        AutoImportRegistryCloneHost autoImportHost = AutoImportRegistryCloneHost.NewAutoImportRegistryCloneHost(
            projectCollection,
            session.parseCache,
            fsBuilder,
            sessionOptions.CurrentDirectory,
            toPath);
        var openFiles = new Dictionary<global::Ts.TsPath.Path, string>(overlays.Count);
        // map-iter: 序无关 — keyed per overlay path.
        foreach ((global::Ts.TsPath.Path path, Overlay overlay) in overlays)
        {
            openFiles[path] = overlay.FileName();
        }
        var prepareAutoImports = new global::Ts.TsPath.Path("");
        if (!string.IsNullOrEmpty(change.AutoImports.Value))
        {
            prepareAutoImports = change.AutoImports.Path(UseCaseSensitiveFileNames());
        }
        global::Ts.Ls.AutoImport.Registry oldAutoImports = AutoImports!;
        if (oldAutoImports is null)
        {
            oldAutoImports = new global::Ts.Ls.AutoImport.Registry(toPath, userPreferences);
        }
        WatchedFiles<Dictionary<global::Ts.TsPath.Path, string>>? autoImportsWatch = null;
        (global::Ts.Ls.AutoImport.Registry? autoImports, Exception? err) = oldAutoImports.Clone(
            ctx.Cancellation,
            new global::Ts.Ls.AutoImport.RegistryChange
            {
                RequestedFile = prepareAutoImports,
                OpenFiles = openFiles,
                Changed = change.FileChanges.Changed,
                Created = change.FileChanges.Created,
                Deleted = change.FileChanges.Deleted,
                RebuiltPrograms = projectsWithNewProgramStructure,
                UserPreferences = change.NewConfig,
            },
            autoImportHost,
            logger!.Fork("UpdateAutoImports"));
        if (err is null)
        {
            autoImportsWatch = this.autoImportsWatch!.Clone(autoImports!.NodeModulesDirectories());
        }

        (SnapshotFS snapshotFS, bool _) = fsBuilder.Finalize();
        Snapshot newSnapshot = NewSnapshot(
            newSnapshotID,
            snapshotFS,
            sessionOptions,
            null,
            compilerOptionsForInferredProjects,
            config,
            autoImports,
            autoImportsWatch,
            toPath);
        newSnapshot.parentId = id;
        newSnapshot.ProjectCollection = projectCollection;
        newSnapshot.ConfigFileRegistry = configFileRegistry;
        newSnapshot.builderLogs = logger;
        newSnapshot.apiError = apiError;

        // map-iter: 序无关 — ref/freeze bookkeeping per project.
        foreach (Project project in newSnapshot.ProjectCollection.Projects())
        {
            if (project.Program is not null)
            {
                session.programCounter.Ref(project.Program);
                if (project.ProgramLastUpdate == newSnapshotID)
                {
                    // If the program was updated during this clone, the project and its host are new
                    // and still retain references to the builder. Freezing clears the builder reference
                    // so it's GC'd and to ensure the project can't access any data not already in the
                    // snapshot during use. This is pretty kludgy, but it's an artifact of Program design:
                    // Program has a single host, which is expected to implement a full vfs.FS, among
                    // other things. That host is *mostly* only used during program *construction*, but a
                    // few methods may get exercised during program *use*. So, our compiler host is allowed
                    // to access caches and perform mutating effects (like acquire referenced project
                    // config files) during snapshot building, and then we call `freeze` to ensure those
                    // mutations don't happen afterwards. In the future, we might improve things by
                    // separating what it takes to build a program from what it takes to use a program,
                    // and only pass the former into NewProgram instead of retaining it indefinitely.
                    project.host!.Freeze(snapshotFS, newSnapshot.ConfigFileRegistry);
                }
            }
        }
        // map-iter: 序无关 — AddOwner bookkeeping per extended config file.
        foreach (var configEntry in newSnapshot.ConfigFileRegistry.configs.Values)
        {
            if (configEntry.commandLine is not null && configEntry.commandLine.ConfigFile is not null)
            {
                // Go: config.commandLine.ConfigFile.SourceFile.ExtendedSourceFiles — the
                // C# TsConfigSourceFile carries the same list directly.
                foreach (string file in configEntry.commandLine.ConfigFile!.ExtendedSourceFiles)
                {
                    session.extendedConfigCache.AddOwner(newSnapshot.toPath(file), newSnapshot.id);
                }
            }
        }

        autoImportHost.Dispose();

        logger.Logf("Finished cloning snapshot {0} into snapshot {1} in {2}", id, newSnapshot.id, start.Elapsed);
        return newSnapshot;
    }

    // ref increments the snapshot's reference count, preventing it from being
    // disposed until a corresponding Deref is called. The snapshot must still
    // be alive (refCount > 0) when ref is called. Only the project Session
    // should call ref(), and it should be done while holding session.snapshotMu.
    internal void Ref()
    {
        if (Interlocked.Increment(ref refCount) <= 1)
        {
            throw new InvalidOperationException($"snapshot {id}: ref on disposed snapshot, parentId={parentId}");
        }
    }

    // tryRef attempts to increment the snapshot's reference count. If the
    // snapshot is already disposed (refCount == 0), it returns false without
    // modifying the count. On success the caller must eventually call Deref.
    internal bool TryRef()
    {
        while (true)
        {
            int rc = Volatile.Read(ref refCount);
            if (rc <= 0)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref refCount, rc + 1, rc) == rc)
            {
                return true;
            }
        }
    }

    // Deref decrements the snapshot's reference count. When the count reaches
    // zero, the snapshot is disposed and its resources are released.
    public void Deref(Session session)
    {
        int rc = Interlocked.Decrement(ref refCount);
        if (rc < 0)
        {
            throw new InvalidOperationException($"snapshot {id}: ref count below zero, parentId={parentId}");
        }
        if (rc == 0)
        {
            Dispose(session);
        }
    }

    // snapshot.go:619-645 — dispose.
    private void Dispose(Session session)
    {
        // map-iter: 序无关 — ref-count bookkeeping per project/program.
        foreach (Project project in ProjectCollection.Projects())
        {
            if (project.Program is not null && session.programCounter.Deref(project.Program))
            {
                // This program is no longer referenced by any snapshot.
                // Mark its checker pool as discarded so its idle-cleanup timer stops
                // keeping the pool alive, allowing the pool and any idle checkers it
                // still references to be reclaimed when the pool is garbage-collected.
                if (project.checkerPool is not null)
                {
                    project.checkerPool.Discard();
                }
                foreach (var file in project.Program.SourceFiles())
                {
                    session.parseCache.Deref(ParseCache.NewParseCacheKey(file.ParseOptions(), file.Hash, file.ScriptKind));
                }
                // snapshot.go:550-552 — duplicate files hold their own parse-cache refs;
                // Program.DuplicateSourceFiles() is now available (Program.cs, program.go:403).
                foreach (var file in project.Program.DuplicateSourceFiles())
                {
                    session.parseCache.Deref(ParseCache.NewParseCacheKey(file.ParseOptions, file.Hash, file.ScriptKind));
                }
            }
        }
        // map-iter: 序无关 — release bookkeeping per config.
        foreach (var config in ConfigFileRegistry.configs.Values)
        {
            if (config.commandLine is not null)
            {
                foreach (string file in config.commandLine!.ExtendedSourceFiles() ?? [])
                {
                    session.extendedConfigCache.Release(session.toPath(file), id);
                }
            }
        }
    }
}

// Shared helpers used by the snapshot/overlay ports (Go spells these inline).
internal static class SnapshotUtil
{
    // Go: xxh3.HashString128(content).
    internal static UInt128 HashString128(string content)
    {
        var h = new System.IO.Hashing.XxHash128();
        h.Append(System.Text.Encoding.UTF8.GetBytes(content));
        return h.GetCurrentHashAsUInt128();
    }

    // Go: openFilePaths (projectcollection.go:203-209) — hosted here because it is
    // consumed by Snapshot.NewSnapshot before ProjectCollection exists.
    internal static global::Ts.Collections.Set<global::Ts.TsPath.Path> OpenFilePaths(Dictionary<global::Ts.TsPath.Path, Overlay> overlays)
    {
        var openFiles = new global::Ts.Collections.Set<global::Ts.TsPath.Path>(overlays.Count);
        // map-iter: 序无关 — set construction from map keys.
        foreach (global::Ts.TsPath.Path path in overlays.Keys)
        {
            openFiles.Add(path);
        }
        return openFiles;
    }

    // Go fmt %v of slices — deterministic [{v1 v2}] style is not needed for C#; keep a
    // stable, readable rendering (log-output only).
    internal static string FormatDocuments(global::Ts.LsProto.DocumentUri[] docs)
    {
        var parts = new string[docs.Length];
        for (int i = 0; i < docs.Length; i++)
        {
            parts[i] = docs[i].Value;
        }
        return "[" + string.Join(" ", parts) + "]";
    }

    internal static string FormatDocuments(global::Ts.Collections.Set<global::Ts.LsProto.DocumentUri> set)
    {
        var parts = new List<string>();
        foreach (var uri in set.Keys())
        {
            parts.Add(uri.Value);
        }
        parts.Sort(StringComparer.Ordinal);
        return "[" + string.Join(" ", parts) + "]";
    }

    internal static string FormatPaths(global::Ts.TsPath.Path[] paths)
    {
        var parts = new string[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            parts[i] = paths[i].Value;
        }
        return "[" + string.Join(" ", parts) + "]";
    }
}

