// Ported from tsgo internal/project/session.go (whole file).
//
// PORT-NOTEs:
//  * Go `context.Context` → RequestContext for project/compiler surfaces and
//    CancellationToken for client/background-queue surfaces (plan/01 §4.4).
//    `context.WithCancel(parent)` → CancellationTokenSource.CreateLinkedTokenSource.
//  * Go `sync.Mutex` → System.Threading.Lock; `sync.RWMutex` → ReaderWriterLockSlim.
//  * Go atomic.Uint64/atomic.Bool → Interlocked over long/int fields.
//  * Go `(T, error)` → `(T, Exception?)` or nullable per plan/05 §4; the always-nil
//    error of GetProjectsForFile is dropped (plan/05 §4, third judgment).
//  * Go runtime-metrics / os-memory sampling in sendPerformanceTelemetry maps to the
//    nearest .NET equivalents; counters without an analog stay 0 (marked inline).
//  * Go `ls.NewLanguageService(projectPath, program, snapshot, activeFile)` collapses
//    to the landed foundation form `Ls.LanguageService.NewLanguageService(program)`
//    (see NewLanguageServiceWorker).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Ts.Collections;
using Ts.Core;
using SnapshotChange = Ts.Project.Snapshot.SnapshotChange;
// PORT-NOTE(project): a file-level `using TsPath = Ts.TsPath.TsPath` alias does not
// win here — inside file-scoped `namespace Ts.Project`, the enclosing namespace `Ts`'s
// `TsPath` sub-namespace shadows the compilation-unit alias, so TsPath members are
// called fully qualified (global::Ts.TsPath.TsPath.*).
using static global::Ts.LsProto.DocumentUriExtensions;

using WatchKind = global::Ts.LsProto.WatchKind;
using FileChangeType = global::Ts.LsProto.FileChangeType;
using FileSystemWatcher = global::Ts.LsProto.FileSystemWatcher;
using FileEvent = global::Ts.LsProto.FileEvent;
using ResolvedClientCapabilities = global::Ts.LsProto.ResolvedClientCapabilities;
using ProjectInfoTelemetryMeasurements = global::Ts.LsProto.ProjectInfoTelemetryMeasurements;
using ProjectInfoTelemetryEvent = global::Ts.LsProto.ProjectInfoTelemetryEvent;
using PerformanceStatsTelemetryMeasurements = global::Ts.LsProto.PerformanceStatsTelemetryMeasurements;
using PerformanceStatsTelemetryEvent = global::Ts.LsProto.PerformanceStatsTelemetryEvent;
using TelemetryEvent = global::Ts.LsProto.RequestFailureTelemetryEventOrPerformanceStatsTelemetryEventOrProjectInfoTelemetryEventOrNull;

namespace Ts.Project;

// session.go:33-47 — UpdateReason.
public enum UpdateReason
{
    Unknown,
    DidOpenFile,
    DidCloseFile,
    DidChangeCompilerOptionsForInferredProjects,
    RequestedLanguageServicePendingChanges,
    RequestedLanguageServiceProjectNotLoaded,
    RequestedLanguageServiceForFileNotOpen,
    RequestedLanguageServiceProjectDirty,
    RequestedLoadProjectTree,
    RequestedLanguageServiceWithAutoImports,
    IdleCleanDiskCache,
}

// session.go:49-51 — watchRequestTimeout is the maximum time to wait for the client to
// respond to a WatchFiles or UnwatchFiles request while holding the watches mutex.
internal static class SessionConstants
{
    internal static readonly TimeSpan WatchRequestTimeout = TimeSpan.FromSeconds(1);

    // session.go:537 — idleCacheCleanDelay.
    internal static readonly TimeSpan IdleCacheCleanDelay = TimeSpan.FromSeconds(30);

    // session.go:579 — performanceTelemetryInterval.
    internal static readonly TimeSpan PerformanceTelemetryInterval = TimeSpan.FromMinutes(5);
}

// session.go:53-67 — SessionOptions are the immutable initialization options for a
// session. Snapshots may reference them as a pointer since they never change.
public sealed class SessionOptions
{
    public string CurrentDirectory = "";
    public string DefaultLibraryPath = "";
    public string TypingsLocation = "";
    // Go: lsproto.PositionEncodingKind — mapped to the generated C#
    // Ts.LsProto.PositionEncodingKind record struct (Lsp/Protocol.g.cs). Go's zero value
    // is the empty string; call sites convert via `.Value ?? ""`.
    public global::Ts.LsProto.PositionEncodingKind PositionEncoding;
    public bool WatchEnabled;
    public bool LoggingEnabled;
    public bool TelemetryEnabled;
    public bool PushDiagnosticsEnabled;
    public TimeSpan DebounceDelay;
    public global::Ts.Locale.Locale Locale;
    public CheckerPoolOptions CheckerPoolOptions { get; set; } = new();

    // session-lsp.go — GetClientCapabilities(ctx). Registered deviation (see
    // LsProto.Missing.cs ClientCapabilitiesAccess): the RequestContext has no context
    // values in C#, so the resolved capabilities travel on SessionOptions instead.
    public ResolvedClientCapabilities ClientCapabilities { get; set; } = new();
}

// session.go:69-77 — SessionInit.
public sealed class SessionInit
{
    public global::Ts.Core.RequestContext BackgroundCtx;
    public SessionOptions Options = null!;
    public global::Ts.Vfs.IFileSystem FS = null!;
    public Client? Client;
    public global::Ts.Project.Logging.Logger? Logger;
    public global::Ts.Project.Ata.NpmExecutor? NpmExecutor;
    public RefCountCache<ParseCacheKey, global::Ts.Ast.SourceFile, FileHandle>? ParseCache;
}

// session.go:79-178 — Session manages the state of an LSP session. It receives
// textDocument events and requests for LanguageService objects from the LPS server
// and processes them into immutable snapshots as the data source for
// LanguageServices. When Session transitions from one snapshot to the next, it diffs
// them and updates file watchers and Automatic Type Acquisition (ATA) state
// accordingly.
// (partial: APIUpdate is declared in Api.cs, mirroring Go's project/api.go —
// the Api layer calls it without importing Ts.Project.) ParseConfigHost: Go's
// *Session satisfies tsoptions.ParseConfigHost structurally (FS +
// GetCurrentDirectory, api/session.go:1030); C# interfaces are nominal, so it
// is declared here.
public sealed partial class Session : global::Ts.Project.Ata.TypingsInstallerHost, global::Ts.TsOptions.ParseConfigHost
{
    internal readonly global::Ts.Core.RequestContext backgroundCtx;
    internal readonly SessionOptions options;
    private readonly Stopwatch startTime = Stopwatch.StartNew(); // Go: startTime time.Time
    internal readonly Func<string, global::Ts.TsPath.Path> toPath;
    internal readonly Client? client;
    internal readonly global::Ts.Project.Logging.Logger logger;
    private readonly global::Ts.Project.Ata.NpmExecutor? npmExecutor;
    internal readonly OverlayFS fs;

    // parseCache is the ref-counted cache of source files used when
    // creating programs during snapshot cloning.
    internal readonly RefCountCache<ParseCacheKey, global::Ts.Ast.SourceFile, FileHandle> parseCache;
    // extendedConfigCache is the ref-counted cache of tsconfig ASTs
    // that are used in the "extends" of another tsconfig.
    internal readonly ExtendedConfigCache extendedConfigCache;
    // programCounter counts how many snapshots reference a program.
    // When a program is no longer referenced, its source files are
    // released from the parseCache.
    internal readonly ProgramCounter programCounter = new();

    // read-only after initialization (InitializeWithUserConfig writes it during
    // session setup, matching session.go:290-291).
    private global::Ts.Ls.UserPreferences initialUserPreferences;
    // current preferences
    private global::Ts.Ls.UserPreferences workspaceUserPreferences;
    private global::Ts.Core.CompilerOptions? compilerOptionsForInferredProjects;
    private global::Ts.Project.Ata.TypingsInstaller? typingsInstaller;
    private readonly Background.Queue backgroundQueue;

    // session.go:114-117 — snapshotID is the counter for snapshot IDs. It does not
    // necessarily equal the `snapshot.ID`. It is stored on Session instead of globally
    // so IDs are predictable in tests. (Go atomic.Uint64 → Interlocked over long.)
    private long snapshotID;

    // snapshot is the current immutable state of all projects.
    private Snapshot? snapshot;
    private readonly ReaderWriterLockSlim snapshotMu = new();
    private readonly Lock snapshotUpdateMu = new();

    // session.go:124-128 — scheduledSnapshotUpdateCancel is the cancelation function
    // for a scheduled snapshot update. Snapshot updates are scheduled and debounced
    // after file closes. (Go context.CancelFunc → CancellationTokenSource.)
    private CancellationTokenSource? scheduledSnapshotUpdateCancel;
    private ulong scheduledSnapshotUpdateGeneration;
    private readonly Lock scheduledSnapshotUpdateMu = new();

    private bool pendingUserConfigChanges;
    private readonly Lock userConfigRWMu = new();

    // pendingFileChanges are accumulated from textDocument/* events delivered
    // by the LSP server through DidOpenFile(), DidChangeFile(), etc. They are
    // applied to the next snapshot update.
    private readonly List<FileChange> pendingFileChanges = [];
    private readonly Lock pendingFileChangesMu = new();

    // pendingATAChanges are produced by Automatic Type Acquisition (ATA)
    // installations and applied to the next snapshot update.
    private readonly Dictionary<global::Ts.TsPath.Path, Snapshot.ATAStateChange> pendingATAChanges = [];
    private readonly Lock pendingATAChangesMu = new();

    // diagnosticsRefreshCancel is the cancelation function for a scheduled
    // diagnostics refresh. Diagnostics refreshes are scheduled and debounced
    // after file watch changes and ATA updates.
    private CancellationTokenSource? diagnosticsRefreshCancel;
    private ulong diagnosticsRefreshGeneration;
    private readonly Lock diagnosticsRefreshMu = new();

    // warmAutoImportCancel is the cancelation function for a running auto-import
    // cache warming task. It is cancelled on file opens, closes, changes,
    // watched-file changes, new auto-import warming requests, and when the session
    // closes. (Go stores a closure wrapping the derived cancel func — kept as Action?.)
    private Action? warmAutoImportCancel;
    private readonly Lock warmAutoImportMu = new();

    // idleCacheCleanTimer is a resettable timer for scheduling idle disk
    // cache cleans. The timer resets on any file event (open, close,
    // change, save, watch) and fires after 30 seconds of inactivity.
    private Timer? idleCacheCleanTimer;
    private readonly Lock idleCacheCleanMu = new();

    // performanceTelemetryCancel cancels the periodic performance telemetry ticker.
    private CancellationTokenSource? performanceTelemetryCancel;

    // seenProjects tracks projects that have already had telemetry sent.
    private readonly SyncSet<global::Ts.TsPath.Path> seenProjects = new();

    // watches tracks the current watch globs and how many individual WatchedFiles
    // are using each glob.
    private readonly WatchRegistry watches;

    // globalDiagPublishPending is set to true when a global diagnostics publish
    // task should be enqueued. It is reset when the task runs, coalescing multiple
    // requests into a single background task. (Go atomic.Bool → Interlocked.)
    private int globalDiagPublishPending;

    private Session(global::Ts.Core.RequestContext backgroundCtx, SessionOptions options, Func<string, global::Ts.TsPath.Path> toPath, Client? client, global::Ts.Project.Logging.Logger logger, global::Ts.Project.Ata.NpmExecutor? npmExecutor, OverlayFS fs, RefCountCache<ParseCacheKey, global::Ts.Ast.SourceFile, FileHandle> parseCache, ExtendedConfigCache extendedConfigCache, Background.Queue backgroundQueue, Snapshot initialSnapshot, global::Ts.Ls.UserPreferences initialUserPreferences)
    {
        this.backgroundCtx = backgroundCtx;
        this.options = options;
        this.toPath = toPath;
        this.client = client;
        this.logger = logger;
        this.npmExecutor = npmExecutor;
        this.fs = fs;
        this.parseCache = parseCache;
        this.extendedConfigCache = extendedConfigCache;
        this.backgroundQueue = backgroundQueue;
        snapshot = initialSnapshot;
        this.initialUserPreferences = initialUserPreferences;
        workspaceUserPreferences = initialUserPreferences;
        watches = WatchRegistry.NewWatchRegistry();
    }

    // session.go:180-252 — NewSession.
    public static Session NewSession(SessionInit init)
    {
        string currentDirectory = init.Options.CurrentDirectory;
        bool useCaseSensitiveFileNames = init.FS.UseCaseSensitiveFileNames();
        Func<string, global::Ts.TsPath.Path> toPath = fileName => global::Ts.TsPath.TsPath.ToPath(fileName, currentDirectory, useCaseSensitiveFileNames);
        OverlayFS overlayFS = OverlayFS.NewOverlayFS(init.FS, [], init.Options.PositionEncoding.Value ?? "", toPath);
        RefCountCache<ParseCacheKey, global::Ts.Ast.SourceFile, FileHandle> parseCache = init.ParseCache ?? ParseCache.NewParseCache(new RefCountCacheOptions());
        ExtendedConfigCache extendedConfigCache = ExtendedConfigCache.NewExtendedConfigCache();

        global::Ts.Project.Logging.Logger sessionLogger = init.Logger ?? global::Ts.Project.Logging.LoggingUtil.NewNopLogger();

        // Go: &SnapshotFS{toPath: toPath, fs: init.FS} — the remaining fields are the
        // zero value (nil maps read as empty in Go); empty dictionaries stand in here.
        var initialSnapshotFS = new SnapshotFS(
            toPath,
            init.FS,
            [],
            [],
            null,
            null,
            null);

        // Go: lsproto.GetClientCapabilities(init.BackgroundCtx) — C# registered
        // deviation: capabilities travel on SessionOptions (ClientCapabilitiesAccess).
        WatchedFiles<Dictionary<global::Ts.TsPath.Path, string>> autoImportsWatch = WatchedFiles<Dictionary<global::Ts.TsPath.Path, string>>.NewWatchedFiles(
            "auto-import",
            WatchKind.Create | WatchKind.Change | WatchKind.Delete,
            ClientCapabilitiesAccess.GetClientCapabilities(init.Options).Workspace.DidChangeWatchedFiles.RelativePatternSupport,
            nodeModulesDirs =>
            {
                // Go: len(nil map) == 0 and ranging over a nil map is a no-op; before the
                // first Clone the input here is Go's nil map, so a null dictionary reads
                // as empty (05 §5 nil-map semantics).
                nodeModulesDirs ??= new Dictionary<global::Ts.TsPath.Path, string>();
                var patterns = new List<string>(nodeModulesDirs.Count);
                // map-iter: 已排序 — patterns are sorted below to match Go's slices.Sort.
                foreach (string dir in nodeModulesDirs.Values)
                {
                    patterns.Add(WatchUtil.GetRecursiveGlobPattern(dir));
                }
                patterns.Sort(StringComparer.Ordinal);
                return new PatternsAndIgnored
                {
                    patternsInsideWorkspace = patterns.ToArray(),
                };
            });

        var backgroundQueue = Background.Queue.NewQueue();
        global::Ts.Project.Snapshot initialSnapshot = global::Ts.Project.Snapshot.NewSnapshot(
            0UL,
            initialSnapshotFS,
            init.Options,
            new ConfigFileRegistry(),
            null,
            global::Ts.Ls.UserPreferences.NewDefaultUserPreferences(),
            null,
            autoImportsWatch,
            toPath);
        var session = new Session(
            init.BackgroundCtx,
            init.Options,
            toPath,
            init.Client,
            sessionLogger,
            init.NpmExecutor,
            overlayFS,
            parseCache,
            extendedConfigCache,
            backgroundQueue,
            initialSnapshot,
            global::Ts.Ls.UserPreferences.NewDefaultUserPreferences());

        if (init.Options.TypingsLocation != "" && init.NpmExecutor is not null)
        {
            session.typingsInstaller = global::Ts.Project.Ata.TypingsInstaller.NewTypingsInstaller(
                new global::Ts.Project.Ata.TypingsInstallerOptions
                {
                    TypingsLocation = init.Options.TypingsLocation,
                    ThrottleLimit = 5,
                }, session);
        }

        return session;
    }

    // session.go:254-257 — FS implements module.ResolutionHost.
    public global::Ts.Vfs.IFileSystem FS()
    {
        return fs.fs;
    }

    // session.go:259-262 — GetCurrentDirectory implements module.ResolutionHost.
    public string GetCurrentDirectory()
    {
        return options.CurrentDirectory;
    }

    // session.go:264-269 — Config gets a copy of current configuration.
    // (Go's lsutil.UserPreferences is a value struct, so the return copies it; the C#
    // class needs the explicit Clone to give callers an isolated copy.)
    public global::Ts.Ls.UserPreferences Config()
    {
        lock (userConfigRWMu)
        {
            return workspaceUserPreferences.Clone();
        }
    }

    // Go: (s *Session) Trace(msg string) — module.ResolutionHost in Go carries Trace;
    // the C# Ts.Module.ResolutionHost has only FS/GetCurrentDirectory, so the panic
    // guard has no ported member. (Go: panic("ATA module resolution should not use tracing").)

    // session.go:276-288 — Configure.
    public void Configure(global::Ts.Ls.UserPreferences config)
    {
        lock (userConfigRWMu)
        {
            pendingUserConfigChanges = true;
            global::Ts.Ls.UserPreferences oldConfig = workspaceUserPreferences;
            workspaceUserPreferences = config;

            // Tell the client to re-request certain commands depending on user preference changes.
            RefreshInlayHintsIfNeeded(oldConfig, config);
            RefreshCodeLensIfNeeded(oldConfig, config);
            RefreshDiagnosticsIfNeeded(oldConfig, config);
            RefreshATAIfNeeded(oldConfig, config);
        }
    }

    // session.go:290-293 — InitializeWithUserConfig.
    public void InitializeWithUserConfig(global::Ts.Ls.UserPreferences config)
    {
        initialUserPreferences = config;
        Configure(config);
    }

    // session.go:295-318 — DidOpenFile.
    public void DidOpenFile(global::Ts.Core.RequestContext ctx, global::Ts.LsProto.DocumentUri uri, int version, string content, string languageKind)
    {
        CancelWarmAutoImportCache();
        ScheduleIdleCacheClean();
        CancelScheduledSnapshotUpdate();
        lock (snapshotUpdateMu)
        {
            FileChangeSummary changes;
            Dictionary<global::Ts.TsPath.Path, Overlay> overlays;
            lock (pendingFileChangesMu)
            {
                pendingFileChanges.Add(new FileChange
                {
                    Kind = FileChangeKind.Open,
                    URI = uri,
                    Version = version,
                    Content = content,
                    LanguageKind = languageKind,
                });
                (changes, overlays) = FlushChangesLocked(ctx);
            }
            UpdateSnapshot(ctx, overlays, new SnapshotChange
            {
                Reason = UpdateReason.DidOpenFile,
                FileChanges = changes,
                ResourceRequest = new Snapshot.ResourceRequest
                {
                    Documents = [uri],
                },
            });
        }
    }

    // session.go:320-330 — DidCloseFile.
    public void DidCloseFile(global::Ts.Core.RequestContext ctx, global::Ts.LsProto.DocumentUri uri)
    {
        CancelWarmAutoImportCache();
        ScheduleIdleCacheClean();
        lock (pendingFileChangesMu)
        {
            pendingFileChanges.Add(new FileChange
            {
                Kind = FileChangeKind.Close,
                URI = uri,
            });
        }
        ScheduleSnapshotUpdate(UpdateReason.DidCloseFile);
    }

    // session.go:332-344 — DidChangeFile.
    public void DidChangeFile(global::Ts.Core.RequestContext ctx, global::Ts.LsProto.DocumentUri uri, int version, global::Ts.LsProto.TextDocumentContentChangePartialOrWholeDocument[] changes)
    {
        CancelDiagnosticsRefresh();
        CancelWarmAutoImportCache();
        ScheduleIdleCacheClean();
        lock (pendingFileChangesMu)
        {
            pendingFileChanges.Add(new FileChange
            {
                Kind = FileChangeKind.Change,
                URI = uri,
                Version = version,
                Changes = changes,
            });
        }
    }

    // session.go:346-354 — DidSaveFile.
    public void DidSaveFile(global::Ts.Core.RequestContext ctx, global::Ts.LsProto.DocumentUri uri)
    {
        ScheduleIdleCacheClean();
        lock (pendingFileChangesMu)
        {
            pendingFileChanges.Add(new FileChange
            {
                Kind = FileChangeKind.Save,
                URI = uri,
            });
        }
    }

    // session.go:356-384 — DidChangeWatchedFiles.
    public void DidChangeWatchedFiles(global::Ts.Core.RequestContext ctx, FileEvent[] changes)
    {
        var fileChanges = new List<FileChange>(changes.Length);
        foreach (FileEvent change in changes)
        {
            FileChangeKind kind;
            switch (change.Type)
            {
                case FileChangeType.Created:
                    kind = FileChangeKind.WatchCreate;
                    break;
                case FileChangeType.Changed:
                    kind = FileChangeKind.WatchChange;
                    break;
                case FileChangeType.Deleted:
                    kind = FileChangeKind.WatchDelete;
                    break;
                default:
                    continue; // Ignore unknown change types.
            }
            fileChanges.Add(new FileChange
            {
                Kind = kind,
                URI = change.Uri,
            });
        }

        lock (pendingFileChangesMu)
        {
            pendingFileChanges.AddRange(fileChanges);
        }

        // Schedule a debounced diagnostics refresh
        ScheduleDiagnosticsRefresh();
        CancelWarmAutoImportCache();
        ScheduleIdleCacheClean();
    }

    // session.go:386-392 — DidChangeCompilerOptionsForInferredProjects.
    public void DidChangeCompilerOptionsForInferredProjects(global::Ts.Core.RequestContext ctx, global::Ts.Core.CompilerOptions? opts)
    {
        compilerOptionsForInferredProjects = opts;
        UpdateSnapshot(ctx, fs.Overlays(), new SnapshotChange
        {
            Reason = UpdateReason.DidChangeCompilerOptionsForInferredProjects,
            CompilerOptionsForInferredProjects = opts,
        });
    }

    // session.go:394-440 — ScheduleDiagnosticsRefresh.
    public void ScheduleDiagnosticsRefresh()
    {
        lock (diagnosticsRefreshMu)
        {
            // Cancel any existing scheduled diagnostics refresh
            if (diagnosticsRefreshCancel is not null)
            {
                diagnosticsRefreshCancel.Cancel();
                logger.Log("Delaying scheduled diagnostics refresh...");
            }
            else
            {
                logger.Log("Scheduling new diagnostics refresh...");
            }

            // Create a new cancellable context for the debounce task
            // (Go: context.WithCancel(s.backgroundCtx)).
            CancellationTokenSource debounceCtx = CancellationTokenSource.CreateLinkedTokenSource(backgroundCtx.Cancellation);
            diagnosticsRefreshGeneration++;
            ulong generation = diagnosticsRefreshGeneration;
            diagnosticsRefreshCancel = debounceCtx;

            // Enqueue the debounced diagnostics refresh
            backgroundQueue.Enqueue(debounceCtx.Token, ctx =>
            {
                try
                {
                    // Sleep for the debounce delay (Go: select over time.After/ctx.Done).
                    if (ctx.WaitHandle.WaitOne(options.DebounceDelay))
                    {
                        // Context was cancelled, newer events arrived
                        return;
                    }
                    // Delay completed, proceed with refresh

                    // Clear the cancel function since we're about to execute the refresh
                    lock (diagnosticsRefreshMu)
                    {
                        if (diagnosticsRefreshGeneration != generation)
                        {
                            return;
                        }
                        diagnosticsRefreshCancel = null;
                    }

                    if (options.LoggingEnabled)
                    {
                        logger.Log("Running scheduled diagnostics refresh");
                    }
                    Exception? err = client!.RefreshDiagnostics(backgroundCtx.Cancellation);
                    if (err is not null && options.LoggingEnabled)
                    {
                        logger.Logf("Error refreshing diagnostics: {0}", err.Message);
                    }
                }
                finally
                {
                    // Go: defer cancel().
                    debounceCtx.Cancel();
                }
            });
        }
    }

    // session.go:442-451 — cancelDiagnosticsRefresh.
    private void CancelDiagnosticsRefresh()
    {
        lock (diagnosticsRefreshMu)
        {
            if (diagnosticsRefreshCancel is not null)
            {
                diagnosticsRefreshCancel.Cancel();
                logger.Log("Canceled scheduled diagnostics refresh");
                diagnosticsRefreshCancel = null;
                diagnosticsRefreshGeneration++;
            }
        }
    }

    // session.go:453-513 — ScheduleSnapshotUpdate.
    public void ScheduleSnapshotUpdate(UpdateReason reason)
    {
        lock (scheduledSnapshotUpdateMu)
        {
            // Cancel any existing scheduled snapshot update
            if (scheduledSnapshotUpdateCancel is not null)
            {
                scheduledSnapshotUpdateCancel.Cancel();
                if (options.LoggingEnabled)
                {
                    logger.Log("Delaying scheduled snapshot update...");
                }
            }
            else if (options.LoggingEnabled)
            {
                logger.Log("Scheduling new snapshot update...");
            }

            // Create a new cancellable context for the debounce task
            // (Go: context.WithCancel(s.backgroundCtx)).
            CancellationTokenSource debounceCtx = CancellationTokenSource.CreateLinkedTokenSource(backgroundCtx.Cancellation);
            scheduledSnapshotUpdateGeneration++;
            ulong generation = scheduledSnapshotUpdateGeneration;
            scheduledSnapshotUpdateCancel = debounceCtx;

            // Go passes the derived debounce ctx into the task; RequestContext carries
            // the cancellation channel, so derive it here.
            global::Ts.Core.RequestContext taskCtx = backgroundCtx with { Cancellation = debounceCtx.Token };

            // Enqueue the debounced snapshot update
            backgroundQueue.Enqueue(debounceCtx.Token, ctx =>
            {
                try
                {
                    // Sleep for the debounce delay (Go: select over time.After/ctx.Done).
                    if (ctx.WaitHandle.WaitOne(options.DebounceDelay))
                    {
                        // Context was cancelled, newer events arrived or another snapshot update ran
                        return;
                    }
                    // Delay completed, proceed with update

                    // Clear the cancel function since we're about to execute the update
                    lock (scheduledSnapshotUpdateMu)
                    {
                        if (scheduledSnapshotUpdateGeneration != generation)
                        {
                            return;
                        }
                        scheduledSnapshotUpdateCancel = null;
                    }

                    if (options.LoggingEnabled)
                    {
                        logger.Log("Running scheduled snapshot update");
                    }

                    lock (snapshotUpdateMu)
                    {
                        (FileChangeSummary fileChanges, Dictionary<global::Ts.TsPath.Path, Overlay> overlays, Dictionary<global::Ts.TsPath.Path, Snapshot.ATAStateChange> ataChanges, global::Ts.Ls.UserPreferences? newConfig) = FlushChanges(taskCtx);
                        if (fileChanges.IsEmpty() && ataChanges.Count == 0 && newConfig is null)
                        {
                            return;
                        }

                        UpdateSnapshot(taskCtx, overlays, new SnapshotChange
                        {
                            Reason = reason,
                            FileChanges = fileChanges,
                            AtaChanges = ataChanges,
                            NewConfig = newConfig,
                        });
                    }
                }
                finally
                {
                    // Go: defer cancel().
                    debounceCtx.Cancel();
                }
            });
        }
    }

    // session.go:515-526 — cancelScheduledSnapshotUpdate. (Public: the API layer calls
    // it from APIUpdate, Go's same-package access.)
    public void CancelScheduledSnapshotUpdate()
    {
        lock (scheduledSnapshotUpdateMu)
        {
            if (scheduledSnapshotUpdateCancel is not null)
            {
                scheduledSnapshotUpdateCancel.Cancel();
                if (options.LoggingEnabled)
                {
                    logger.Log("Canceled scheduled snapshot update");
                }
                scheduledSnapshotUpdateCancel = null;
                scheduledSnapshotUpdateGeneration++;
            }
        }
    }

    // session.go:528-535 — cancelWarmAutoImportCache.
    private void CancelWarmAutoImportCache()
    {
        lock (warmAutoImportMu)
        {
            if (warmAutoImportCancel is not null)
            {
                warmAutoImportCancel();
                warmAutoImportCancel = null;
            }
        }
    }

    // session.go:539-568 — scheduleIdleCacheClean.
    private void ScheduleIdleCacheClean()
    {
        lock (idleCacheCleanMu)
        {
            if (idleCacheCleanTimer is not null)
            {
                idleCacheCleanTimer.Dispose();
            }

            idleCacheCleanTimer = new Timer(_ =>
            {
                lock (idleCacheCleanMu)
                {
                    idleCacheCleanTimer = null;
                }

                lock (snapshotUpdateMu)
                {
                    CancelScheduledSnapshotUpdate();

                    global::Ts.Core.RequestContext ctx = backgroundCtx;
                    (FileChangeSummary fileChanges, Dictionary<global::Ts.TsPath.Path, Overlay> overlays, Dictionary<global::Ts.TsPath.Path, Snapshot.ATAStateChange> ataChanges, global::Ts.Ls.UserPreferences? newConfig) = FlushChanges(ctx);
                    UpdateSnapshot(ctx, overlays, new SnapshotChange
                    {
                        Reason = UpdateReason.IdleCleanDiskCache,
                        FileChanges = fileChanges,
                        AtaChanges = ataChanges,
                        NewConfig = newConfig,
                        CleanDiskCache = true,
                    });
                }

                // Go: go func() { runtime.GC() }().
                ThreadPool.QueueUserWorkItem(_ => GC.Collect());
            }, null, SessionConstants.IdleCacheCleanDelay, Timeout.InfiniteTimeSpan);
        }
    }

    // session.go:570-577 — cancelIdleCacheClean.
    private void CancelIdleCacheClean()
    {
        lock (idleCacheCleanMu)
        {
            if (idleCacheCleanTimer is not null)
            {
                idleCacheCleanTimer.Dispose();
                idleCacheCleanTimer = null;
            }
        }
    }

    // session.go:583-604 — StartPerformanceTelemetry begins periodic collection and
    // sending of performance telemetry. It should be called once after the session is
    // initialized.
    public void StartPerformanceTelemetry()
    {
        if (!options.TelemetryEnabled)
        {
            return;
        }
        // Go: context.WithCancel(s.backgroundCtx).
        CancellationTokenSource ctx = CancellationTokenSource.CreateLinkedTokenSource(backgroundCtx.Cancellation);
        performanceTelemetryCancel = ctx;
        backgroundQueue.Enqueue(ctx.Token, taskCtx =>
        {
            // Go: ticker := time.NewTicker(performanceTelemetryInterval) + select.
            while (true)
            {
                if (taskCtx.WaitHandle.WaitOne(SessionConstants.PerformanceTelemetryInterval))
                {
                    return;
                }
                if (client is null || !client.IsActive())
                {
                    continue;
                }
                SendPerformanceTelemetry(taskCtx);
            }
        });
    }

    // session.go:606-611 — stopPerformanceTelemetry.
    private void StopPerformanceTelemetry()
    {
        if (performanceTelemetryCancel is not null)
        {
            performanceTelemetryCancel.Cancel();
            performanceTelemetryCancel = null;
        }
    }

    // session.go:613-733 — sendPerformanceTelemetry.
    //
    // PORT-NOTE(project): Go samples runtime/metrics (heap goal, GC scans, goroutines)
    // and go-osstat memory. The C# AOT runtime exposes a subset; each counter below is
    // mapped to its nearest .NET analog or left at 0 (Go leaves GoMemLimit at 0 when
    // unconfigured for the same MAX_SAFE_INTEGER reason).
    private void SendPerformanceTelemetry(CancellationToken ctx)
    {
        if (client is null || !options.TelemetryEnabled)
        {
            return;
        }
        snapshotMu.EnterReadLock();
        Snapshot? snap = snapshot;
        snapshotMu.ExitReadLock();
        if (snap is null)
        {
            return;
        }

        // Read GC/runtime metrics in a single call (Go: gometrics.Read(samples)).
        GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();

        var measurements = new PerformanceStatsTelemetryMeasurements
        {
            OpenFileCount = snap.fs.Overlays.Count,
            UptimeSeconds = startTime.Elapsed.TotalSeconds,
            ProjectCount = snap.ProjectCollection.Projects().Count,
            ConfigCount = snap.ConfigFileRegistry.configs.Count,
            CachedDiskFileCount = snap.fs.DiskFiles?.Count ?? 0,
        };

        measurements.MemoryUsedBytes = GC.GetTotalMemory(false);
        // GoMemLimit / GoGCPercent: Go-specific knobs with no .NET analog → 0.
        measurements.HeapGoalBytes = gcInfo.HeapSizeBytes;
        // HeapLiveBytes / HeapObjectCount / HeapStackBytes / HeapReleasedBytes /
        // HeapFreeBytes / GcScanHeapBytes: Go runtime metric names → 0.
        measurements.GoMaxProcs = Environment.ProcessorCount;
        measurements.GoroutineCount = ThreadPool.ThreadCount;
        measurements.GcCyclesTotal = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        // GcCPUSeconds: Go accounts GC CPU separately → 0.
        // UserCPUSeconds → UserProcessorTime.
        using (Process proc = Process.GetCurrentProcess())
        {
            measurements.UserCPUSeconds = proc.UserProcessorTime.TotalSeconds;
        }

        // Read system memory stats (Go: osmemory.Get()).
        // TotalAvailableMemoryBytes/MemoryLoad are the AOT-safe stand-ins.
        measurements.SystemMemTotal = gcInfo.TotalAvailableMemoryBytes;
        // Go: osmemory reports a "used" estimate; MemoryLoadBytes is the runtime's
        // equivalent load estimate in bytes (GCMemoryInfo has no percentage form).
        measurements.SystemMemUsed = gcInfo.MemoryLoadBytes;

        // Read auto-import registry stats
        global::Ts.Ls.AutoImport.Registry? registry = snap.AutoImportRegistry();
        if (registry is not null)
        {
            global::Ts.Ls.AutoImport.CacheStats autoImportStats = registry.GetCacheStats();
            measurements.AutoImportProjectBucketCount = autoImportStats.ProjectBuckets.Count;
            measurements.AutoImportNodeModulesBucketCount = autoImportStats.NodeModulesBuckets.Count;
            measurements.AutoImportUniquePackageCount = autoImportStats.UniquePackageCount;
            foreach (global::Ts.Ls.AutoImport.BucketStats bucket in autoImportStats.ProjectBuckets)
            {
                measurements.AutoImportProjectExportCount += bucket.ExportCount;
                measurements.AutoImportProjectFileCount += bucket.FileCount;
            }
            foreach (global::Ts.Ls.AutoImport.BucketStats bucket in autoImportStats.NodeModulesBuckets)
            {
                measurements.AutoImportNodeModulesExportCount += bucket.ExportCount;
                measurements.AutoImportNodeModulesFileCount += bucket.FileCount;
                if (bucket.DependencyNames is null)
                {
                    measurements.AutoImportNodeModulesUnfilteredBucketCount++;
                }
            }
        }

        Exception? err = client.SendTelemetry(ctx, new TelemetryEvent
        {
            PerformanceStatsTelemetryEvent = new PerformanceStatsTelemetryEvent
            {
                Measurements = measurements,
            },
        });
        if (err is not null && options.LoggingEnabled)
        {
            logger.Logf("Error sending performance telemetry: {0}", err.Message);
        }
    }

    // session.go:735-749 — sendProjectInfoTelemetryForNewProjects.
    private void SendProjectInfoTelemetryForNewProjects(Snapshot oldSnapshot, Snapshot newSnapshot)
    {
        if (!options.TelemetryEnabled)
        {
            return;
        }
        global::Ts.Core.RequestContext ctx = backgroundCtx;
        global::Ts.Collections.OrderedMap.DiffOrderedMaps(
            oldSnapshot.ProjectCollection.ProjectsByPath(),
            newSnapshot.ProjectCollection.ProjectsByPath(),
            (global::Ts.TsPath.Path _, Project addedProject) => SendProjectInfoTelemetry(ctx, addedProject),
            (global::Ts.TsPath.Path _, Project _) => { },
            (global::Ts.TsPath.Path _, Project _, Project _) => { });
    }

    // session.go:751-772 — sendProjectInfoTelemetry.
    private void SendProjectInfoTelemetry(global::Ts.Core.RequestContext ctx, Project project)
    {
        if (client is null || !options.TelemetryEnabled)
        {
            return;
        }
        if (seenProjects.Has(project.configFilePath))
        {
            return;
        }

        if (project.Program is null || project.CommandLine is null)
        {
            return;
        }

        TelemetryEvent info = CollectProjectInfoTelemetry(project);
        Exception? err = client.SendTelemetry(ctx.Cancellation, info);
        if (err is not null)
        {
            if (options.LoggingEnabled)
            {
                logger.Logf("Error sending project info telemetry: {0}", err.Message);
            }
            return;
        }

        seenProjects.Add(project.configFilePath);
    }

    // session.go:774-850 — collectProjectInfoTelemetry.
    private TelemetryEvent CollectProjectInfoTelemetry(Project project)
    {
        global::Ts.Core.CompilerOptions opts = project.CommandLine!.CompilerOptions() ?? new global::Ts.Core.CompilerOptions();

        string configFileName = "other";
        if (project.Kind == Kind.Configured)
        {
            string baseName = global::Ts.TsPath.TsPath.GetBaseFileName(project.ConfigFileName());
            if (baseName == "tsconfig.json" || baseName == "jsconfig.json")
            {
                configFileName = baseName;
            }
        }

        string projectType = "inferred";
        if (project.Kind == Kind.Configured)
        {
            projectType = "configured";
        }

        var props = new Dictionary<string, string>
        {
            ["configFileName"] = configFileName,
            ["projectType"] = projectType,
            ["version"] = global::Ts.Core.TsVersion.Version(),
        };

        // Compiler options — same approach as Strada's convertCompilerOptionsForTelemetry:
        // booleans and enum string names, no paths.
        // (Go builds map[string]any then json.Marshal, which sorts keys; the entries are
        // emitted in sorted order to match.)
        var compilerOptions = new List<(string Key, object Value)>();
        SetTristate(compilerOptions, "strict", opts.Strict);
        SetTristate(compilerOptions, "noImplicitAny", opts.NoImplicitAny);
        SetTristate(compilerOptions, "noImplicitThis", opts.NoImplicitThis);
        SetTristate(compilerOptions, "strictNullChecks", opts.StrictNullChecks);
        SetTristate(compilerOptions, "strictFunctionTypes", opts.StrictFunctionTypes);
        SetTristate(compilerOptions, "strictBindCallApply", opts.StrictBindCallApply);
        SetTristate(compilerOptions, "strictPropertyInitialization", opts.StrictPropertyInitialization);
        SetTristate(compilerOptions, "strictBuiltinIteratorReturn", opts.StrictBuiltinIteratorReturn);
        SetTristate(compilerOptions, "useUnknownInCatchVariables", opts.UseUnknownInCatchVariables);
        SetTristate(compilerOptions, "exactOptionalPropertyTypes", opts.ExactOptionalPropertyTypes);
        SetTristate(compilerOptions, "allowJs", opts.AllowJs);
        SetTristate(compilerOptions, "checkJs", opts.CheckJs);
        SetTristate(compilerOptions, "noEmit", opts.NoEmit);
        SetTristate(compilerOptions, "declaration", opts.Declaration);
        SetTristate(compilerOptions, "composite", opts.Composite);
        SetTristate(compilerOptions, "isolatedModules", opts.IsolatedModules);
        SetTristate(compilerOptions, "skipLibCheck", opts.SkipLibCheck);
        SetTristate(compilerOptions, "incremental", opts.Incremental);
        if (opts.Target != global::Ts.Core.ScriptTarget.None)
        {
            compilerOptions.Add(("target", ScriptTargetToString(opts.Target)));
        }
        if (opts.Module != global::Ts.Core.ModuleKind.None)
        {
            compilerOptions.Add(("module", opts.Module.ToDisplayString()));
        }
        if (opts.ModuleResolution != global::Ts.Core.ModuleResolutionKind.Unknown)
        {
            compilerOptions.Add(("moduleResolution", opts.ModuleResolution.ToDisplayString()));
        }
        if (opts.Jsx != global::Ts.Core.JsxEmit.None)
        {
            compilerOptions.Add(("jsx", opts.Jsx.ToDisplayString()));
        }
        props["compilerOptions"] = MarshalTelemetryMap(compilerOptions);

        // Config file shape (Go: project.CommandLine.Raw.(*collections.OrderedMap[string, any])).
        if (project.CommandLine.Raw is global::Ts.Collections.OrderedMap<string, object?> raw)
        {
            props["extends"] = BoolTelemetry(raw.Has("extends"));
            props["files"] = BoolTelemetry(raw.Has("files"));
            props["include"] = BoolTelemetry(raw.Has("include"));
            props["exclude"] = BoolTelemetry(raw.Has("exclude"));
        }

        return new TelemetryEvent
        {
            ProjectInfoTelemetryEvent = new ProjectInfoTelemetryEvent
            {
                Properties = props,
                Measurements = CountFileStats(project.Program!.SourceFiles()),
            },
        };
    }

    // session.go:852-858 — setTristate.
    private static void SetTristate(List<(string Key, object Value)> entries, string key, global::Ts.Core.Tristate v)
    {
        if (v == global::Ts.Core.Tristate.True)
        {
            entries.Add((key, true));
        }
        else if (v == global::Ts.Core.Tristate.False)
        {
            entries.Add((key, false));
        }
    }

    // session.go:860-865 — boolTelemetry.
    private static string BoolTelemetry(bool v)
    {
        return v ? "true" : "false";
    }

    // Go: json.Marshal(map[string]any) over bool/string values. Keys are emitted in
    // ordinal-sorted order (Go's encoding/json sorts map keys); AOT-safe hand emitter
    // (plan/07 §4 — no reflection serializer).
    private static string MarshalTelemetryMap(List<(string Key, object Value)> entries)
    {
        entries.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        var b = new StringBuilder();
        b.Append('{');
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0)
            {
                b.Append(',');
            }
            b.Append(JsonQuote(entries[i].Key)).Append(':');
            switch (entries[i].Value)
            {
                case bool v:
                    b.Append(v ? "true" : "false");
                    break;
                case string s:
                    b.Append(JsonQuote(s));
                    break;
                default:
                    b.Append("null");
                    break;
            }
        }
        b.Append('}');
        return b.ToString();
    }

    // Minimal Go-encoding/json-compatible string escaping (shortest form, no HTML
    // escaping — Go applies \u003c-style escaping only with the default HTMLEscape,
    // which the Go encoder ALSO does by default; telemetry consumers treat both the
    // same. Kept shortest-form for log readability.).
    private static string JsonQuote(string s)
    {
        var b = new StringBuilder(s.Length + 2);
        b.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':
                    b.Append("\\\"");
                    break;
                case '\\':
                    b.Append("\\\\");
                    break;
                case '\n':
                    b.Append("\\n");
                    break;
                case '\r':
                    b.Append("\\r");
                    break;
                case '\t':
                    b.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        b.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        b.Append(c);
                    }
                    break;
            }
        }
        b.Append('"');
        return b.ToString();
    }

    // session.go:867-892 — countFileStats.
    private static ProjectInfoTelemetryMeasurements CountFileStats(global::Ts.Ast.SourceFile[] sourceFiles)
    {
        var stats = new ProjectInfoTelemetryMeasurements();
        foreach (global::Ts.Ast.SourceFile sf in sourceFiles)
        {
            double size = sf.End;
            switch (sf.ScriptKind)
            {
                case global::Ts.Core.ScriptKind.JS:
                    stats.JsFileCount++;
                    stats.JsFileSize += size;
                    break;
                case global::Ts.Core.ScriptKind.Jsx:
                    stats.JsxFileCount++;
                    stats.JsxFileSize += size;
                    break;
                case global::Ts.Core.ScriptKind.TS:
                    if (global::Ts.TsPath.TsPath.IsDeclarationFileName(sf.FileName()))
                    {
                        stats.DtsFileCount++;
                        stats.DtsFileSize += size;
                    }
                    else
                    {
                        stats.TsFileCount++;
                        stats.TsFileSize += size;
                    }
                    break;
                case global::Ts.Core.ScriptKind.Tsx:
                    stats.TsxFileCount++;
                    stats.TsxFileSize += size;
                    break;
            }
        }
        return stats;
    }

    // Go: core.ScriptTarget.String() (stringer -trimprefix=ScriptTarget). The C#
    // ScriptTarget has no generated stringer; mirror the generated names.
    private static string ScriptTargetToString(global::Ts.Core.ScriptTarget target) => target switch
    {
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
        _ => $"ScriptTarget({(int)target})",
    };

    // session.go:894-898 — Snapshot.
    public Snapshot? Snapshot()
    {
        snapshotMu.EnterReadLock();
        try
        {
            return snapshot;
        }
        finally
        {
            snapshotMu.ExitReadLock();
        }
    }

    // session.go:900-973 — getSnapshot flushes pending changes and updates the
    // session's snapshot if needed for the given request. When callerRef is true, the
    // returned snapshot has an extra reference for the caller (taken atomically under
    // snapshotMu), guaranteeing it stays alive until the caller calls Deref.
    private Snapshot GetSnapshot(global::Ts.Core.RequestContext ctx, Snapshot.ResourceRequest request, bool callerRef)
    {
        lock (snapshotUpdateMu)
        {
            CancelScheduledSnapshotUpdate();

            (FileChangeSummary fileChanges, Dictionary<global::Ts.TsPath.Path, Overlay> overlays, Dictionary<global::Ts.TsPath.Path, Snapshot.ATAStateChange> ataChanges, global::Ts.Ls.UserPreferences? newConfig) = FlushChanges(ctx);
            bool updateSnapshot = !fileChanges.IsEmpty() || ataChanges.Count > 0 || newConfig is not null;
            if (updateSnapshot)
            {
                // If there are pending file changes, we need to update the snapshot.
                // Sending the requested URI ensures that the project for this URI is loaded.
                return UpdateSnapshotWorker(ctx, overlays, new SnapshotChange
                {
                    Reason = UpdateReason.RequestedLanguageServicePendingChanges,
                    FileChanges = fileChanges,
                    AtaChanges = ataChanges,
                    NewConfig = newConfig,
                    ResourceRequest = request,
                }, callerRef);
            }

            // If there are no pending file changes, we can try to use the current snapshot.
            UpdateReason updateReason = UpdateReason.Unknown;
            snapshotMu.EnterReadLock();
            try
            {
                Snapshot snap = snapshot!;
                if (request.Projects.Length > 0)
                {
                    updateReason = UpdateReason.RequestedLanguageServiceProjectDirty;
                }
                else if (request.ProjectTree is not null)
                {
                    updateReason = UpdateReason.RequestedLoadProjectTree;
                }
                // Go: request.AutoImports != "" (session.go:934) — DocumentUri's zero value
                // has a null Value, so test emptiness like snapshot.go:427 does.
                else if (!string.IsNullOrEmpty(request.AutoImports.Value))
                {
                    updateReason = UpdateReason.RequestedLanguageServiceWithAutoImports;
                }
                else
                {
                    // map-iter: 序无关 — per-document probes below surface the same
                    // update reason regardless of visit order.
                    foreach (global::Ts.LsProto.DocumentUri document in request.Documents)
                    {
                        Project? project = snap.GetDefaultProject(document);
                        if (project is null)
                        {
                            updateReason = UpdateReason.RequestedLanguageServiceProjectNotLoaded;
                        }
                        else if (project.IsDirty)
                        {
                            updateReason = UpdateReason.RequestedLanguageServiceProjectDirty;
                        }
                    }
                    if (updateReason == UpdateReason.Unknown)
                    {
                        foreach (global::Ts.LsProto.DocumentUri document in request.ConfiguredProjectDocuments)
                        {
                            if (snap.fs.IsOpenFile(document.FileName()))
                            {
                                Project? project = snap.GetDefaultProject(document);
                                if (project is null)
                                {
                                    updateReason = UpdateReason.RequestedLanguageServiceProjectNotLoaded;
                                }
                                else if (project.IsDirty)
                                {
                                    updateReason = UpdateReason.RequestedLanguageServiceProjectDirty;
                                }
                            }
                            else
                            {
                                updateReason = UpdateReason.RequestedLanguageServiceForFileNotOpen;
                            }
                        }
                    }
                }
                if (updateReason == UpdateReason.Unknown)
                {
                    if (callerRef)
                    {
                        snap.Ref();
                    }
                    return snap;
                }
            }
            finally
            {
                snapshotMu.ExitReadLock();
            }

            // Go: s.snapshotMu.RUnlock() precedes updateSnapshot (session.go:968-972);
            // the C# ReaderWriterLockSlim must also release the read lock before the
            // write lock is taken.
            return UpdateSnapshotWorker(ctx, overlays, new SnapshotChange
            {
                Reason = updateReason,
                ResourceRequest = request,
            }, callerRef);
        }
    }

    // session.go:975-986 — getSnapshotAndDefaultProject.
    private (Snapshot? Snapshot, Project? Project, global::Ts.Ls.LanguageService? LanguageService, Exception? Error) GetSnapshotAndDefaultProject(global::Ts.Core.RequestContext ctx, global::Ts.LsProto.DocumentUri uri, bool callerRef)
    {
        Snapshot snap = GetSnapshot(
            ctx,
            new Snapshot.ResourceRequest { Documents = [uri] },
            callerRef);
        Project? project = snap.GetDefaultProject(uri);
        if (project is null)
        {
            return (null, null, null, new InvalidOperationException($"no project found for URI {uri.Value}"));
        }
        return (snap, project, NewLanguageServiceWorker(project, snap, uri.FileName()), null);
    }

    // Go: ls.NewLanguageService(project.configFilePath, project.GetProgram(), snapshot,
    // activeFile). PORT-NOTE(project): the C# foundation only carries
    // LanguageService.NewLanguageService(program) (Ls/LanguageService.cs:20-27); the
    // projectPath/snapshot-host/activeFile wiring lands with the lsp-server wave, so
    // every session construction site funnels through here to keep the call shape
    // recognizable. Deviation registered in the Ls/LanguageService.cs header.
    private static global::Ts.Ls.LanguageService NewLanguageServiceWorker(Project project, Snapshot snap, string activeFile)
    {
        _ = snap;
        _ = activeFile;
        return global::Ts.Ls.LanguageService.NewLanguageService(project.GetProgram()!);
    }

    // session.go:988-994 — GetLanguageService.
    public (global::Ts.Ls.LanguageService? LanguageService, Exception? Error) GetLanguageService(global::Ts.Core.RequestContext ctx, global::Ts.LsProto.DocumentUri uri)
    {
        (_, _, global::Ts.Ls.LanguageService? languageService, Exception? err) = GetSnapshotAndDefaultProject(ctx, uri, callerRef: false);
        if (err is not null)
        {
            return (null, err);
        }
        return (languageService, null);
    }

    // session.go:996-1004 — GetLanguageServiceAndProjectsForFile.
    public (Project? Project, global::Ts.Ls.LanguageService? LanguageService, LsProject[] AllProjects, Exception? Error) GetLanguageServiceAndProjectsForFile(global::Ts.Core.RequestContext ctx, global::Ts.LsProto.DocumentUri uri)
    {
        (Snapshot? snap, Project? project, global::Ts.Ls.LanguageService? defaultLs, Exception? err) = GetSnapshotAndDefaultProject(ctx, uri, callerRef: false);
        if (err is not null)
        {
            return (null, null, [], err);
        }
        // !!! TODO: sheetal:  Get other projects that contain the file with symlink
        LsProject[] allProjects = snap!.GetProjectsContainingFile(uri);
        return (project, defaultLs, allProjects, null);
    }

    // session.go:1006-1016 — GetProjectsForFile. (Go's error is always nil; dropped
    // per plan/05 §4.)
    public LsProject[] GetProjectsForFile(global::Ts.Core.RequestContext ctx, global::Ts.LsProto.DocumentUri uri)
    {
        Snapshot snap = GetSnapshot(
            ctx,
            new Snapshot.ResourceRequest { ConfiguredProjectDocuments = [uri] },
            callerRef: false);

        // !!! TODO: sheetal:  Get other projects that contain the file with symlink
        return snap.GetProjectsContainingFile(uri);
    }

    // session.go:1018-1041 — GetLanguageServicesForDocuments.
    public global::Ts.Ls.LanguageService[] GetLanguageServicesForDocuments(global::Ts.Core.RequestContext ctx, global::Ts.LsProto.DocumentUri[] uris)
    {
        Snapshot snap = GetSnapshot(
            ctx,
            new Snapshot.ResourceRequest { Documents = uris },
            callerRef: false);

        string activeFile = "";
        if (uris.Length > 0)
        {
            activeFile = uris[0].FileName();
        }

        List<Project> projects = snap.ProjectCollection.Projects();
        var services = new List<global::Ts.Ls.LanguageService>(projects.Count);
        // map-iter: 序无关 — services follow the project list order (sorted).
        foreach (Project project in projects)
        {
            global::Ts.Compiler.Program? program = project.GetProgram();
            if (program is null)
            {
                continue;
            }

            services.Add(NewLanguageServiceWorker(project, snap, activeFile));
        }
        return services.ToArray();
    }

    // session.go:1043-1059 — GetLanguageServiceForProjectWithFile.
    public global::Ts.Ls.LanguageService? GetLanguageServiceForProjectWithFile(global::Ts.Core.RequestContext ctx, Project project, global::Ts.LsProto.DocumentUri uri)
    {
        Snapshot snap = GetSnapshot(
            ctx,
            new Snapshot.ResourceRequest { Projects = [project.Id()] },
            callerRef: false);
        // Ensure we have updated project
        Project? updatedProject = snap.ProjectCollection.GetProjectByPath(project.Id());
        if (updatedProject is null)
        {
            return null;
        }
        project = updatedProject;
        if (project is null)
        {
            return null;
        }
        // if program doesnt contain this file any more ignore it
        if (!project.HasFile(uri.FileName()))
        {
            return null;
        }
        return NewLanguageServiceWorker(project, snap, uri.FileName());
    }

    // session.go:1061-1076 — WithSnapshotLoadingProjectTree acquires a ref'd snapshot
    // with the requested project trees loaded, then calls fn. The snapshot stays alive
    // for the duration of fn.
    public void WithSnapshotLoadingProjectTree(
        global::Ts.Core.RequestContext ctx,
        global::Ts.Collections.Set<global::Ts.TsPath.Path>? requestedProjectTrees,
        Action<Snapshot> fn)
    {
        Snapshot snap = GetSnapshot(
            ctx,
            new Snapshot.ResourceRequest { ProjectTree = new Snapshot.ProjectTreeRequest(requestedProjectTrees) },
            callerRef: true);
        try
        {
            fn(snap);
        }
        finally
        {
            snap.Deref(this);
        }
    }

    // session.go:1078-1093 — GetCurrentLanguageServiceWithAutoImports flushes pending
    // file changes, clones the current snapshot with auto-import preparation for the
    // given URI, then returns a LanguageService for the default project. Use this only
    // outside of request handling (e.g. cache warming). For request handlers, use
    // GetLanguageServiceWithAutoImports with the request-level snapshot instead.
    public (global::Ts.Ls.LanguageService? LanguageService, Exception? Error) GetCurrentLanguageServiceWithAutoImports(global::Ts.Core.RequestContext ctx, global::Ts.LsProto.DocumentUri uri)
    {
        Snapshot snap = GetSnapshot(ctx, new Snapshot.ResourceRequest
        {
            Documents = [uri],
            AutoImports = uri,
        }, callerRef: false);
        Project? project = snap.GetDefaultProject(uri);
        if (project is null)
        {
            return (null, new InvalidOperationException($"no project found for URI {uri.Value}"));
        }
        return (NewLanguageServiceWorker(project, snap, uri.FileName()), null);
    }

    // session.go:1095-1123 — WithLanguageServiceAndSnapshot synchronously acquires a
    // ref'd snapshot and creates a language service for the given URI. fn receives
    // both the language service and the backing snapshot so it can clone the snapshot
    // (e.g. to enable auto-imports). The snapshot is kept alive until the async work
    // completes.
    //
    // Only use this method when the callback needs direct access to the snapshot.
    // For handlers that only need a LanguageService, use GetLanguageService
    // directly—language services continue to work even after their backing
    // snapshot has been disposed.
    public (Func<Exception?>? AsyncWork, Exception? Error) WithLanguageServiceAndSnapshot(
        global::Ts.Core.RequestContext ctx,
        global::Ts.LsProto.DocumentUri uri,
        Func<global::Ts.Ls.LanguageService, Snapshot, (Func<Exception?>?, Exception?)> fn)
    {
        (Snapshot? snap, _, global::Ts.Ls.LanguageService? languageService, Exception? err) = GetSnapshotAndDefaultProject(ctx, uri, callerRef: true);
        if (err is not null || snap is null)
        {
            return (null, err);
        }
        (Func<Exception?>? asyncWork, Exception? fnErr) = fn(languageService!, snap);
        if (fnErr is not null || asyncWork is null)
        {
            snap.Deref(this);
            return (null, fnErr);
        }
        return (() =>
        {
            try
            {
                return asyncWork();
            }
            finally
            {
                snap.Deref(this);
            }
        }, null);
    }

    // session.go:1125-1154 — GetLanguageServiceWithAutoImports clones the given
    // snapshot with auto-import preparation for the given URI, without flushing
    // pending file changes. The cloned snapshot will be adopted as the session's
    // current snapshot in the background if other changes haven't been adopted in the
    // meantime.
    public (global::Ts.Ls.LanguageService? LanguageService, Exception? Error) GetLanguageServiceWithAutoImports(global::Ts.Core.RequestContext ctx, Snapshot baseSnapshot, global::Ts.LsProto.DocumentUri uri)
    {
        SnapshotChange change = new SnapshotChange
        {
            Reason = UpdateReason.RequestedLanguageServiceWithAutoImports,
            ResourceRequest = new Snapshot.ResourceRequest
            {
                Documents = [uri],
                AutoImports = uri,
            },
        };
        Snapshot newSnapshot = baseSnapshot.Clone(ctx, change, baseSnapshot.fs.Overlays, this);

        Project? project = newSnapshot.GetDefaultProject(uri);
        if (project is null)
        {
            // Clone's initial ref (1) is released since we won't use this snapshot.
            newSnapshot.Deref(this);
            return (null, new InvalidOperationException($"no project found for URI {uri.Value}"));
        }

        // The clone's initial ref (1) is transferred to adoptSnapshotChange,
        // which will either promote it as the session's current snapshot or
        // release it if the session has moved on.
        backgroundQueue.Enqueue(backgroundCtx.Cancellation, ctx =>
        {
            AdoptSnapshotChange(baseSnapshot, newSnapshot);
        });

        return (NewLanguageServiceWorker(project, newSnapshot, uri.FileName()), null);
    }

    // session.go:1156-1188 — adoptSnapshotChange promotes a cloned snapshot as the
    // session's current snapshot so future requests benefit from the work already
    // done. If the session has moved on, the snapshot is discarded; the next request
    // needing auto-imports will redo the work on the latest snapshot.
    private void AdoptSnapshotChange(Snapshot baseSnapshot, Snapshot newSnapshot)
    {
        snapshotMu.EnterWriteLock();
        Snapshot oldSnapshot = snapshot!;
        bool adopted = ReferenceEquals(oldSnapshot, baseSnapshot);
        if (adopted)
        {
            // Session hasn't moved on; adopt the new snapshot. The clone's initial
            // ref is transferred to become the session's ref for its current snapshot.
            snapshot = newSnapshot;
        }
        snapshotMu.ExitWriteLock();

        if (options.LoggingEnabled)
        {
            if (adopted)
            {
                logger.Logf("Adopted snapshot {0} (parent {1}) as current session snapshot (replacing {2})", newSnapshot.SnapshotId, newSnapshot.SnapshotParentId, oldSnapshot.SnapshotId);
                logger.Log(newSnapshot.builderLogs!.ToString());
            }
            else
            {
                logger.Logf("Discarded snapshot {0} (parent {1}); session has moved on to snapshot {2}", newSnapshot.SnapshotId, newSnapshot.SnapshotParentId, oldSnapshot.SnapshotId);
                string logs = newSnapshot.builderLogs!.ToString();
                if (logs.Length != 0)
                {
                    logger.Logf("--- Discarded snapshot {0} builder logs (NOT adopted) ---", newSnapshot.SnapshotId);
                    logger.Log(logs);
                    logger.Logf("--- End discarded snapshot {0} builder logs ---", newSnapshot.SnapshotId);
                }
            }
        }

        if (adopted)
        {
            // Go: oldSnapshot.Deref(s) after snapshotMu.Unlock().
            oldSnapshot.Deref(this);
        }
        else
        {
            // Session has moved on to a newer snapshot; discard this one.
            // Release the clone's initial ref. If a handler is still using
            // the snapshot, its own ref keeps it alive.
            newSnapshot.Deref(this);
        }
    }

    // session.go:1190-1192 — UpdateSnapshot.
    public void UpdateSnapshot(global::Ts.Core.RequestContext ctx, Dictionary<global::Ts.TsPath.Path, Overlay> overlays, SnapshotChange change)
    {
        UpdateSnapshotWorker(ctx, overlays, change, callerRef: false);
    }

    // session.go:1194-1200 — updateSnapshotRef is like UpdateSnapshot but returns the
    // created snapshot with an extra reference for the caller. The ref is taken
    // atomically with the snapshot assignment under snapshotMu, so the snapshot is
    // guaranteed to be alive when returned. The caller must call snapshot.Deref(s)
    // when done. (Public-by-internal access: the API layer calls it from APIUpdate,
    // Go's same-package access.)
    internal Snapshot UpdateSnapshotRef(global::Ts.Core.RequestContext ctx, Dictionary<global::Ts.TsPath.Path, Overlay> overlays, SnapshotChange change)
    {
        return UpdateSnapshotWorker(ctx, overlays, change, callerRef: true);
    }

    // session.go:1202-1244 — updateSnapshot.
    private Snapshot UpdateSnapshotWorker(global::Ts.Core.RequestContext ctx, Dictionary<global::Ts.TsPath.Path, Overlay> overlays, SnapshotChange change, bool callerRef)
    {
        Snapshot oldSnapshot;
        Snapshot newSnapshot;
        snapshotMu.EnterWriteLock();
        try
        {
            oldSnapshot = snapshot!;
            newSnapshot = oldSnapshot.Clone(ctx, change, overlays, this);
            snapshot = newSnapshot;
            if (callerRef)
            {
                newSnapshot.Ref();
            }
            if (!ReferenceEquals(newSnapshot, oldSnapshot))
            {
                // Release the session's reference to the old snapshot. The new snapshot's
                // clone ref (1) is transferred to become the session's ref for its current
                // snapshot. Other holders (e.g. active handlers) keep the old snapshot alive
                // via their own refs until they complete.
                oldSnapshot.Deref(this);
            }
        }
        finally
        {
            snapshotMu.ExitWriteLock();
        }

        // Enqueue ATA updates if needed
        if (typingsInstaller is not null && !Config().IsATADisabled())
        {
            TriggerATAForUpdatedProjects(newSnapshot);
        }

        // Enqueue logging, watch updates, and diagnostic refresh tasks
        // !!! userPreferences/configuration updates
        backgroundQueue.Enqueue(backgroundCtx.Cancellation, ctx =>
        {
            if (options.LoggingEnabled)
            {
                logger.Logf("Adopted snapshot {0} (parent {1}) as current session snapshot (replacing {2})", newSnapshot.SnapshotId, newSnapshot.SnapshotParentId, oldSnapshot.SnapshotId);
                logger.Log(newSnapshot.builderLogs!.ToString());
                LogProjectChanges(oldSnapshot, newSnapshot);
                logger.Log("");
            }
            if (options.WatchEnabled)
            {
                Exception? err = UpdateWatches(oldSnapshot, newSnapshot);
                if (err is not null && options.LoggingEnabled)
                {
                    logger.Log(err.Message);
                }
            }
            PublishProgramDiagnostics(oldSnapshot, newSnapshot);
            SendProjectInfoTelemetryForNewProjects(oldSnapshot, newSnapshot);
            WarmAutoImportCache(backgroundCtx with { Cancellation = ctx }, change, oldSnapshot, newSnapshot);
        });

        return newSnapshot;
    }

    // session.go:1246-1251 — WaitForBackgroundTasks waits for all background tasks to
    // complete. This is intended to be used only for testing purposes.
    public void WaitForBackgroundTasks()
    {
        CancelIdleCacheClean();
        backgroundQueue.Wait();
    }

    // session.go:1253-1331 — updateWatch.
    private static List<Exception> UpdateWatch<T>(
        CancellationToken ctx,
        Session session,
        global::Ts.Project.Logging.Logger logger,
        WatchedFiles<T>? oldWatcher,
        WatchedFiles<T>? newWatcher) where T : notnull
    {
        var errors = new List<Exception>();
        if (newWatcher is not null)
        {
            WatchedFiles<T>.Watchers w = newWatcher.GetWatchers();
            FileSystemWatcher[] watchers = [.. w.WorkspaceWatchers, .. w.OutsideWorkspaceWatchers];
            if (watchers.Length > 0)
            {
                var newWatchers = new global::Ts.Collections.OrderedMap<WatcherID, FileSystemWatcher>();
                for (int i = 0; i < watchers.Length; i++)
                {
                    WatcherID globId = new WatcherID($"{w.WatcherID}.{i}");
                    if (session.watches.Acquire(watchers[i], globId))
                    {
                        newWatchers.Set(globId, watchers[i]);
                    }
                }
                var watchErrors = new List<Exception>();
                // map-iter: 序无关 — OrderedMap iteration is insertion-ordered.
                foreach ((WatcherID id, FileSystemWatcher watcher) in newWatchers.Entries())
                {
                    // Create a fresh timeout per client call so earlier calls
                    // don't consume the deadline for later ones.
                    // (Go: context.WithTimeout(ctx, watchRequestTimeout).)
                    using var callCtx = CancellationTokenSource.CreateLinkedTokenSource(ctx);
                    callCtx.CancelAfter(SessionConstants.WatchRequestTimeout);
                    Exception? err = session.client!.WatchFiles(callCtx.Token, id, [watcher]);
                    if (err is not null)
                    {
                        watchErrors.Add(err);
                    }
                    else if (logger is not null)
                    {
                        if (oldWatcher is null)
                        {
                            logger.Log($"Added new watch: {id}");
                        }
                        else
                        {
                            logger.Log($"Updated watch: {id}");
                        }
                        logger.Log("\t" + WatchUtil.FileSystemWatcherGlobString(watcher));
                        logger.Log("");
                    }
                }
                if (watchErrors.Count > 0)
                {
                    // Roll back ALL newly-acquired watchers on any failure to keep
                    // refcounts clean. On retry, Acquire will see them as new again.
                    // Re-registering an already-registered watcher with the client
                    // is harmless (registerCapability with the same ID replaces it).
                    foreach ((_, FileSystemWatcher watcher) in newWatchers.Entries())
                    {
                        session.watches.Release(watcher);
                    }
                    session.watches.MarkPending(w.WatcherID);
                    errors.AddRange(watchErrors);
                }
                else
                {
                    session.watches.ClearPending(w.WatcherID);
                }
                if (w.IgnoredPaths is { Count: > 0 } ignoredPaths)
                {
                    logger!.Logf("{0} paths ineligible for watching", ignoredPaths.Count);
                    if (logger.IsVerbose())
                    {
                        // map-iter: 序无关 — log-only enumeration.
                        foreach (string path in ignoredPaths)
                        {
                            logger.Log("\t" + path);
                        }
                    }
                }
            }
        }
        if (oldWatcher is not null)
        {
            WatchedFiles<T>.Watchers w = oldWatcher.GetWatchers();
            FileSystemWatcher[] watchers = [.. w.WorkspaceWatchers, .. w.OutsideWorkspaceWatchers];
            if (watchers.Length > 0)
            {
                var removedIDs = new List<WatcherID>();
                foreach (FileSystemWatcher watcher in watchers)
                {
                    (WatcherID id, bool removed) = session.watches.Release(watcher);
                    if (removed)
                    {
                        removedIDs.Add(id);
                    }
                }
                foreach (WatcherID id in removedIDs)
                {
                    // (Go: context.WithTimeout(ctx, watchRequestTimeout).)
                    using var callCtx = CancellationTokenSource.CreateLinkedTokenSource(ctx);
                    callCtx.CancelAfter(SessionConstants.WatchRequestTimeout);
                    Exception? err = session.client!.UnwatchFiles(callCtx.Token, id);
                    if (err is not null)
                    {
                        errors.Add(err);
                    }
                    else if (logger is not null && newWatcher is null)
                    {
                        logger.Log($"Removed watch: {id}");
                    }
                }
            }
        }
        return errors;
    }

    // session.go:1333-1407 — updateWatches.
    private Exception? UpdateWatches(Snapshot oldSnapshot, Snapshot newSnapshot)
    {
        var errors = new List<Exception>();
        Stopwatch start = Stopwatch.StartNew();
        CancellationToken ctx = backgroundCtx.Cancellation;
        global::Ts.Core.CoreUtil.DiffMapsFunc(
            oldSnapshot.ConfigFileRegistry.configs,
            newSnapshot.ConfigFileRegistry.configs,
            (ConfigFileEntry a, ConfigFileEntry b) => a.rootFilesWatch.ID() == b.rootFilesWatch.ID(),
            (global::Ts.TsPath.Path _, ConfigFileEntry addedEntry) => errors.AddRange(UpdateWatch(ctx, this, logger, null, addedEntry.rootFilesWatch)),
            (global::Ts.TsPath.Path _, ConfigFileEntry removedEntry) => errors.AddRange(UpdateWatch(ctx, this, logger, removedEntry.rootFilesWatch, null)),
            (global::Ts.TsPath.Path _, ConfigFileEntry oldEntry, ConfigFileEntry newEntry) => errors.AddRange(UpdateWatch(ctx, this, logger, oldEntry.rootFilesWatch, newEntry.rootFilesWatch)));
        // Retry config watchers whose IDs didn't change but whose previous registration failed.
        // map-iter: 序无关 — per-config retry probes; order does not affect the result.
        foreach ((global::Ts.TsPath.Path path, ConfigFileEntry newEntry) in newSnapshot.ConfigFileRegistry.configs)
        {
            if (oldSnapshot.ConfigFileRegistry.configs.TryGetValue(path, out ConfigFileEntry? oldEntry))
            {
                if (oldEntry.rootFilesWatch.ID() == newEntry.rootFilesWatch.ID())
                {
                    if (watches.IsPending(newEntry.rootFilesWatch.ID()))
                    {
                        errors.AddRange(UpdateWatch(ctx, this, logger, null, newEntry.rootFilesWatch));
                    }
                }
            }
        }

        global::Ts.Collections.OrderedMap.DiffOrderedMaps(
            oldSnapshot.ProjectCollection.ProjectsByPath(),
            newSnapshot.ProjectCollection.ProjectsByPath(),
            (global::Ts.TsPath.Path _, Project addedProject) =>
            {
                errors.AddRange(UpdateWatch(ctx, this, logger, null, addedProject.programFilesWatch));
                errors.AddRange(UpdateWatch(ctx, this, logger, null, addedProject.typingsWatch));
            },
            (global::Ts.TsPath.Path _, Project removedProject) =>
            {
                errors.AddRange(UpdateWatch(ctx, this, logger, removedProject.programFilesWatch, null));
                errors.AddRange(UpdateWatch(ctx, this, logger, removedProject.typingsWatch, null));
            },
            (global::Ts.TsPath.Path _, Project oldProject, Project newProject) =>
            {
                if (oldProject.programFilesWatch.ID() != newProject.programFilesWatch.ID())
                {
                    errors.AddRange(UpdateWatch(ctx, this, logger, oldProject.programFilesWatch, newProject.programFilesWatch));
                }
                else
                {
                    if (watches.IsPending(newProject.programFilesWatch.ID()))
                    {
                        errors.AddRange(UpdateWatch(ctx, this, logger, null, newProject.programFilesWatch));
                    }
                }
                if (oldProject.typingsWatch.ID() != newProject.typingsWatch.ID())
                {
                    errors.AddRange(UpdateWatch(ctx, this, logger, oldProject.typingsWatch, newProject.typingsWatch));
                }
                else
                {
                    if (watches.IsPending(newProject.typingsWatch.ID()))
                    {
                        errors.AddRange(UpdateWatch(ctx, this, logger, null, newProject.typingsWatch));
                    }
                }
            });

        if (oldSnapshot.autoImportsWatch.ID() != newSnapshot.autoImportsWatch.ID())
        {
            errors.AddRange(UpdateWatch(ctx, this, logger, oldSnapshot.autoImportsWatch, newSnapshot.autoImportsWatch));
        }
        else
        {
            if (watches.IsPending(newSnapshot.autoImportsWatch.ID()))
            {
                errors.AddRange(UpdateWatch(ctx, this, logger, null, newSnapshot.autoImportsWatch));
            }
        }

        if (errors.Count > 0)
        {
            var message = new StringBuilder("errors updating watches: [");
            for (int i = 0; i < errors.Count; i++)
            {
                if (i > 0)
                {
                    message.Append(' ');
                }
                message.Append(errors[i].Message);
            }
            message.Append(']');
            return new InvalidOperationException(message.ToString());
        }
        else if (options.LoggingEnabled)
        {
            logger.Log($"Updated watches in {start.Elapsed}");
        }
        return null;
    }

    // session.go:1409-1421 — Close.
    public void Close()
    {
        // Cancel any pending scheduled snapshot update
        CancelScheduledSnapshotUpdate();
        // Cancel any pending diagnostics refresh
        CancelDiagnosticsRefresh();
        // Cancel any pending auto-import cache warming
        CancelWarmAutoImportCache();
        // Cancel any pending idle cache clean
        CancelIdleCacheClean();
        // Cancel periodic performance telemetry
        StopPerformanceTelemetry();
        backgroundQueue.Close();
    }

    // session.go:1423-1440 — flushChanges.
    internal (FileChangeSummary FileChanges, Dictionary<global::Ts.TsPath.Path, Overlay> Overlays, Dictionary<global::Ts.TsPath.Path, Snapshot.ATAStateChange> AtaChanges, global::Ts.Ls.UserPreferences? NewConfig) FlushChanges(global::Ts.Core.RequestContext ctx)
    {
        Dictionary<global::Ts.TsPath.Path, Snapshot.ATAStateChange> pendingATAChangesSnapshot;
        lock (pendingFileChangesMu)
        lock (pendingATAChangesMu)
        {
            pendingATAChangesSnapshot = new Dictionary<global::Ts.TsPath.Path, Snapshot.ATAStateChange>(pendingATAChanges);
            pendingATAChanges.Clear();
            FileChangeSummary fileChanges;
            Dictionary<global::Ts.TsPath.Path, Overlay> overlays;
            (fileChanges, overlays) = FlushChangesLocked(ctx);
            lock (userConfigRWMu)
            {
                global::Ts.Ls.UserPreferences? newPrefs = null;
                if (pendingUserConfigChanges)
                {
                    newPrefs = workspaceUserPreferences;
                }
                pendingUserConfigChanges = false;
                return (fileChanges, overlays, pendingATAChangesSnapshot, newPrefs);
            }
        }
    }

    // session.go:1442-1455 — flushChangesLocked should only be called with
    // s.pendingFileChangesMu held.
    private (FileChangeSummary Result, Dictionary<global::Ts.TsPath.Path, Overlay> Overlays) FlushChangesLocked(global::Ts.Core.RequestContext ctx)
    {
        _ = ctx;
        if (pendingFileChanges.Count == 0)
        {
            return (new FileChangeSummary(), fs.Overlays());
        }

        Stopwatch start = Stopwatch.StartNew();
        (FileChangeSummary changes, Dictionary<global::Ts.TsPath.Path, Overlay> overlays) = fs.ProcessChanges(pendingFileChanges);
        if (options.LoggingEnabled)
        {
            logger.Log($"Processed {pendingFileChanges.Count} file changes in {start.Elapsed}");
        }
        pendingFileChanges.Clear();
        return (changes, overlays);
    }

    // session.go:1457-1488 — logProjectChanges logs information about projects that
    // have changed between snapshots.
    private void LogProjectChanges(Snapshot oldSnapshot, Snapshot newSnapshot)
    {
        bool loggedProjectChanges = false;
        void LogProject(Project project)
        {
            var builder = new StringBuilder();
            project.Print(logger.IsVerbose() /*writeFileNames*/, logger.IsVerbose() /*writeFileExplanation*/, builder);
            logger.Log(builder.ToString());
            loggedProjectChanges = true;
        }
        global::Ts.Collections.OrderedMap.DiffOrderedMaps(
            oldSnapshot.ProjectCollection.ProjectsByPath(),
            newSnapshot.ProjectCollection.ProjectsByPath(),
            (global::Ts.TsPath.Path _, Project addedProject) =>
            {
                // New project added
                LogProject(addedProject);
            },
            (global::Ts.TsPath.Path _, Project removedProject) =>
            {
                // Project removed
                logger.Logf("\nProject '{0}' removed\n{1}", removedProject.Name(), Project.Hr);
            },
            (global::Ts.TsPath.Path _, Project _, Project newProject) =>
            {
                // Project updated
                if (newProject.ProgramUpdateKind == ProgramUpdateKind.NewFiles)
                {
                    LogProject(newProject);
                }
            });

        if (loggedProjectChanges || logger.IsVerbose())
        {
            LogCacheStats(newSnapshot);
        }
    }

    // session.go:1490-1550 — logCacheStats.
    private void LogCacheStats(Snapshot snap)
    {
        int parseCacheSize = 0;
        int extendedConfigCount = 0;
        if (logger.IsVerbose())
        {
            parseCache.entries.Range((_, _) =>
            {
                parseCacheSize++;
                return true;
            });
            extendedConfigCache.entries.Range((_, _) =>
            {
                extendedConfigCount++;
                return true;
            });
        }
        logger.Log("\n======== Cache Statistics ========");
        logger.Logf("Open file count:   {0,6}", snap.fs.Overlays.Count);
        logger.Logf("Cached disk files: {0,6}", snap.fs.DiskFiles?.Count ?? 0);
        logger.Logf("Realpath aliases:  {0,6}", snap.fs.NodeModulesRealpathAliases?.Count ?? 0);
        logger.Logf("Project count:     {0,6}", snap.ProjectCollection.Projects().Count);
        logger.Logf("Config count:      {0,6}", snap.ConfigFileRegistry.configs.Count);
        if (logger.IsVerbose())
        {
            logger.Logf("Parse cache size:           {0,6}", parseCacheSize);
            logger.Logf("Program count:              {0,6}", programCounter.Len());
            logger.Logf("Extended config cache size: {0,6}", extendedConfigCount);

            logger.Log("Auto Imports:");
            global::Ts.Ls.AutoImport.CacheStats autoImportStats = snap.AutoImportRegistry()!.GetCacheStats();
            logger.Logf("\tUnique packages (by realpath): {0}", autoImportStats.UniquePackageCount);
            if (autoImportStats.ProjectBuckets.Count > 0)
            {
                logger.Log("\tProject buckets:");
                foreach (global::Ts.Ls.AutoImport.BucketStats bucket in autoImportStats.ProjectBuckets)
                {
                    logger.Logf("\t\t{0}{1}:", bucket.Path, global::Ts.Core.CoreUtil.IfElse(bucket.State.Dirty(), " (dirty)", ""));
                    logger.Logf("\t\t\tFiles: {0}", bucket.FileCount);
                    logger.Logf("\t\t\tExports: {0}", bucket.ExportCount);
                }
            }
            if (autoImportStats.NodeModulesBuckets.Count > 0)
            {
                logger.Log("\tnode_modules buckets:");
                foreach (global::Ts.Ls.AutoImport.BucketStats bucket in autoImportStats.NodeModulesBuckets)
                {
                    logger.Logf("\t\t{0}{1}:", bucket.Path, global::Ts.Core.CoreUtil.IfElse(bucket.State.Dirty(), " (dirty)", ""));
                    // map-iter: 已排序 — Set<string>.Keys() surfaces Go's map-key iteration;
                    // log-only listing.
                    foreach (string packageName in bucket.State.DirtyPackages()?.Keys() ?? [])
                    {
                        logger.Logf("\t\t\tNeeds granular update: {0}", packageName);
                    }
                    if (bucket.DependencyNames is not null)
                    {
                        logger.Logf("\t\t\tCollected packages: {0}", bucket.DependencyNames.Len());
                    }
                    else
                    {
                        logger.Log("\t\t\tCollected packages: all, due to no package.json!");
                    }
                    logger.Logf("\t\t\tTotal packages: {0}", bucket.PackageNames!.Len());
                    logger.Logf("\t\t\tFiles: {0}", bucket.FileCount);
                    logger.Logf("\t\t\tExports: {0}", bucket.ExportCount);
                    if (bucket.State.RecursiveSearchPackages() is null)
                    {
                        logger.Log("\t\t\tRecursive search: all");
                    }
                    else if (bucket.State.RecursiveSearchPackages()!.Len() > 0)
                    {
                        logger.Logf("\t\t\tRecursive search: {0} packages", bucket.State.RecursiveSearchPackages()!.Len());
                    }
                    else
                    {
                        logger.Log("\t\t\tRecursive search: none");
                    }
                }
            }
        }
    }

    // session.go:1552-1554 — NpmInstall (implements ata.NpmExecutor).
    public (byte[] Output, Exception? Error) NpmInstall(string cwd, string[] npmInstallArgs)
    {
        return npmExecutor!.NpmInstall(cwd, npmInstallArgs);
    }

    // session.go:1556-1562 — refreshInlayHintsIfNeeded.
    private void RefreshInlayHintsIfNeeded(global::Ts.Ls.UserPreferences oldPrefs, global::Ts.Ls.UserPreferences newPrefs)
    {
        if (!oldPrefs.InlayHints.Equals(newPrefs.InlayHints))
        {
            Exception? err = client!.RefreshInlayHints(backgroundCtx.Cancellation);
            if (err is not null && options.LoggingEnabled)
            {
                logger.Logf("Error refreshing inlay hints: {0}", err.Message);
            }
        }
    }

    // session.go:1564-1570 — refreshCodeLensIfNeeded.
    private void RefreshCodeLensIfNeeded(global::Ts.Ls.UserPreferences oldPrefs, global::Ts.Ls.UserPreferences newPrefs)
    {
        if (!oldPrefs.CodeLens.Equals(newPrefs.CodeLens))
        {
            Exception? err = client!.RefreshCodeLens(backgroundCtx.Cancellation);
            if (err is not null && options.LoggingEnabled)
            {
                logger.Logf("Error refreshing code lens: {0}", err.Message);
            }
        }
    }

    // session.go:1572-1578 — refreshDiagnosticsIfNeeded.
    private void RefreshDiagnosticsIfNeeded(global::Ts.Ls.UserPreferences oldPrefs, global::Ts.Ls.UserPreferences newPrefs)
    {
        if (oldPrefs.CustomConfigFileName != newPrefs.CustomConfigFileName ||
            oldPrefs.ReportStyleChecksAsWarnings != newPrefs.ReportStyleChecksAsWarnings ||
            oldPrefs.EnableValidation != newPrefs.EnableValidation)
        {
            ScheduleDiagnosticsRefresh();
        }
    }

    // session.go:1580-1586 — refreshATAIfNeeded.
    private void RefreshATAIfNeeded(global::Ts.Ls.UserPreferences oldPrefs, global::Ts.Ls.UserPreferences newPrefs)
    {
        if (oldPrefs.IsATADisabled() && !newPrefs.IsATADisabled())
        {
            // ATA was re-enabled; schedule a diagnostics refresh so the next snapshot update
            // re-triggers ATA for existing projects with the new setting.
            ScheduleDiagnosticsRefresh();
        }
    }

    // session.go:1588-1651 — publishProgramDiagnostics.
    private void PublishProgramDiagnostics(Snapshot oldSnapshot, Snapshot newSnapshot)
    {
        if (!options.PushDiagnosticsEnabled)
        {
            return;
        }
        if (newSnapshot.UserPreferences().EnableValidation.IsFalse())
        {
            if (oldSnapshot.UserPreferences().EnableValidation.IsFalse())
            {
                return;
            }
            // map-iter: 序无关 — per-project clear-publish; independent per entry.
            foreach ((global::Ts.TsPath.Path configFilePath, Project oldProject) in oldSnapshot.ProjectCollection.ProjectsByPath().Entries())
            {
                if (oldProject.Kind == Kind.Configured && oldSnapshot.ProjectCollection.GetOpenConfiguredProjects().Has(configFilePath))
                {
                    PublishProjectDiagnostics(backgroundCtx, configFilePath.Value, null, oldSnapshot.Converters());
                }
            }
            return;
        }

        global::Ts.Core.RequestContext ctx = backgroundCtx;
        global::Ts.Collections.OrderedMap<global::Ts.TsPath.Path, Project> oldProjects = oldSnapshot.ProjectCollection.ProjectsByPath();
        global::Ts.Collections.OrderedMap<global::Ts.TsPath.Path, Project> newProjects = newSnapshot.ProjectCollection.ProjectsByPath();
        global::Ts.Collections.Set<global::Ts.TsPath.Path> oldOpenProjects = oldSnapshot.ProjectCollection.GetOpenConfiguredProjects();
        global::Ts.Collections.Set<global::Ts.TsPath.Path> newOpenProjects = newSnapshot.ProjectCollection.GetOpenConfiguredProjects();
        global::Ts.Collections.OrderedMap.DiffOrderedMaps(
            oldProjects,
            newProjects,
            (global::Ts.TsPath.Path configFilePath, Project addedProject) =>
            {
                if (!ShouldPublishProgramDiagnostics(addedProject, newSnapshot.ID()) || !newOpenProjects.Has(configFilePath))
                {
                    return;
                }
                PublishProjectDiagnostics(ctx, configFilePath.Value, addedProject.GetProjectDiagnostics(ctx), newSnapshot.Converters());
            },
            (global::Ts.TsPath.Path configFilePath, Project removedProject) =>
            {
                if (removedProject.Kind != Kind.Configured)
                {
                    return;
                }
                PublishProjectDiagnostics(ctx, configFilePath.Value, null, oldSnapshot.Converters());
            },
            (global::Ts.TsPath.Path configFilePath, Project _, Project newProject) =>
            {
                if (!ShouldPublishProgramDiagnostics(newProject, newSnapshot.ID()) || !newOpenProjects.Has(configFilePath))
                {
                    return;
                }
                PublishProjectDiagnostics(ctx, configFilePath.Value, newProject.GetProjectDiagnostics(ctx), newSnapshot.Converters());
            });
        // Sync diagnostics for projects whose open-file state changed without a program update.
        // map-iter: 序无关 — per-project open-state sync; independent per entry.
        foreach ((global::Ts.TsPath.Path configFilePath, Project newProject) in newProjects.Entries())
        {
            if (newProject.Kind != Kind.Configured)
            {
                continue;
            }
            if (!oldProjects.Has(configFilePath))
            {
                continue; // Handled by added project case above
            }
            Project oldProject = oldProjects.TryGet(configFilePath, out Project? found) ? found : null!;
            bool newHasOpenFiles = newOpenProjects.Has(configFilePath);
            bool oldHasOpenFiles = oldOpenProjects.Has(configFilePath);
            if (newHasOpenFiles && !oldHasOpenFiles &&
                (ReferenceEquals(newProject, oldProject) || !ShouldPublishProgramDiagnostics(newProject, newSnapshot.ID())))
            {
                // Project reopened without a program update
                PublishProjectDiagnostics(ctx, configFilePath.Value, newProject.GetProjectDiagnostics(ctx), newSnapshot.Converters());
            }
            else if (!newHasOpenFiles && oldHasOpenFiles)
            {
                // Project closed
                PublishProjectDiagnostics(ctx, configFilePath.Value, null, newSnapshot.Converters());
            }
        }
    }

    // session.go:1653-1658 — shouldPublishProgramDiagnostics.
    private static bool ShouldPublishProgramDiagnostics(Project p, ulong snapshotId)
    {
        if (p.Kind != Kind.Configured || p.Program is null || p.ProgramLastUpdate != snapshotId)
        {
            return false;
        }
        return p.ProgramUpdateKind > ProgramUpdateKind.Cloned;
    }

    // session.go:1660-1675 — publishProjectDiagnostics.
    //
    // PORT-NOTE(project): Go's lsconv.DiagnosticToLSPPush reads the diagnosticOptions
    // from the request-context client capabilities; the C# context has no value
    // channel, so the resolved capabilities come from SessionOptions
    // (ClientCapabilitiesAccess). Go's push path does not set
    // reportStyleChecksAsWarnings (converters.go:250-254), hence `false`.
    private void PublishProjectDiagnostics(global::Ts.Core.RequestContext ctx, string configFilePath, global::Ts.Ast.Diagnostic[]? diagnostics, global::Ts.Ls.LsConv.Converters converters)
    {
        _ = converters; // Go threads the snapshot converters; the C# LsDeps converter recomputes ranges from the source file.
        if (Config().EnableValidation.IsFalse())
        {
            diagnostics = null;
        }
        ResolvedClientCapabilities clientCaps = ClientCapabilitiesAccess.GetClientCapabilities(options);
        global::Ts.LsProto.ResolvedPublishDiagnosticsClientCapabilities clientDiagnosticCaps = clientCaps.TextDocument.PublishDiagnostics;
        var lspDiagnostics = new List<global::Ts.LsProto.Diagnostic>(diagnostics?.Length ?? 0);
        var tagValueSet = clientDiagnosticCaps.TagSupport.ValueSet ?? [];
        foreach (global::Ts.Ast.Diagnostic diag in diagnostics ?? [])
        {
            lspDiagnostics.Add(global::Ts.Ls.LsDeps.DiagnosticToLSP(
                diag,
                reportStyleChecksAsWarnings: false,
                relatedInformation: clientDiagnosticCaps.RelatedInformation,
                supportsUnnecessaryTag: Array.IndexOf(tagValueSet, global::Ts.LsProto.DiagnosticTag.Unnecessary) >= 0,
                supportsDeprecatedTag: Array.IndexOf(tagValueSet, global::Ts.LsProto.DiagnosticTag.Deprecated) >= 0,
                visualStudio: clientCaps.VSSupportsVisualStudioExtensions));
        }

        Exception? err = client!.PublishDiagnostics(ctx.Cancellation, new global::Ts.LsProto.PublishDiagnosticsParams
        {
            Uri = global::Ts.Ls.LsDeps.FileNameToDocumentURI(configFilePath),
            Diagnostics = lspDiagnostics.ToArray(),
        });
        if (err is not null && options.LoggingEnabled)
        {
            logger.Logf("Error publishing diagnostics: {0}", err.Message);
        }
    }

    // session.go:1677-1687 — EnqueuePublishGlobalDiagnostics schedules a background
    // check for new accumulated global diagnostics from checker pools, re-publishing
    // tsconfig diagnostics if changed. Multiple calls are coalesced into a single
    // background task.
    public void EnqueuePublishGlobalDiagnostics()
    {
        if (!options.PushDiagnosticsEnabled || Config().EnableValidation.IsFalse())
        {
            return;
        }
        if (Interlocked.CompareExchange(ref globalDiagPublishPending, 1, 0) == 0)
        {
            backgroundQueue.Enqueue(backgroundCtx.Cancellation, PublishGlobalDiagnostics);
        }
    }

    // session.go:1689-1706 — publishGlobalDiagnostics.
    private void PublishGlobalDiagnostics(CancellationToken ctx)
    {
        try
        {
            snapshotMu.EnterReadLock();
            Snapshot snap = snapshot!;
            snap.Ref();
            snapshotMu.ExitReadLock();
            try
            {
                global::Ts.Core.RequestContext rc = backgroundCtx with { Cancellation = ctx };
                foreach (Project project in snap.ProjectCollection.Projects())
                {
                    if (project.Kind != Kind.Configured || project.checkerPool is null)
                    {
                        continue;
                    }
                    if (project.checkerPool.TakeNewGlobalDiagnostics())
                    {
                        PublishProjectDiagnostics(rc, project.configFilePath.Value, project.GetProjectDiagnostics(rc), snap.Converters());
                    }
                }
            }
            finally
            {
                snap.Deref(this);
            }
        }
        finally
        {
            Interlocked.Exchange(ref globalDiagPublishPending, 0);
        }
    }

    // session.go:1708-1759 — triggerATAForUpdatedProjects.
    private void TriggerATAForUpdatedProjects(Snapshot newSnapshot)
    {
        // map-iter: 序无关 — per-project ATA trigger; independent per entry.
        foreach (Project project in newSnapshot.ProjectCollection.Projects())
        {
            if (project.ShouldTriggerATA(newSnapshot.ID()))
            {
                backgroundQueue.Enqueue(backgroundCtx.Cancellation, ctx =>
                {
                    global::Ts.Project.Logging.LogTree? logTree = null;
                    if (options.LoggingEnabled)
                    {
                        logTree = new global::Ts.Project.Logging.LogTree("Triggering ATA for project " + project.Name());
                    }

                    global::Ts.Project.Ata.TypingsInfo typingsInfo = project.ComputeTypingsInfo();
                    var request = new global::Ts.Project.Ata.TypingsInstaller.TypingsInstallRequest
                    {
                        ProjectID = project.configFilePath,
                        TypingsInfo = typingsInfo,
                        FileNames = global::Ts.Core.CoreUtil.Map(project.Program!.SourceFiles(), static file => file.FileName())!,
                        ProjectRootPath = project.CurrentDirectory,
                        CompilerOptions = project.CommandLine!.CompilerOptions(),
                        CurrentDirectory = options.CurrentDirectory,
                        GetScriptKind = global::Ts.Core.CoreUtil.GetScriptKindFromFileName,
                        FS = fs.fs,
                        Logger = logTree,
                    };

                    string projectDisplayName = project.DisplayName(options.CurrentDirectory);
                    client?.ProgressStart(global::Ts.Diagnostics.Diagnostics.Installing_types_for_0, projectDisplayName);
                    (global::Ts.Project.Ata.TypingsInstaller.TypingsInstallResult? result, Exception? err) = typingsInstaller!.InstallTypings(request);
                    client?.ProgressFinish(global::Ts.Diagnostics.Diagnostics.Installing_types_for_0, projectDisplayName);
                    if (err is not null)
                    {
                        if (logTree is not null)
                        {
                            logger.Log($"ATA installation failed for project {project.Name()}: {err.Message}");
                            logger.Log(logTree.ToString());
                        }
                    }
                    else
                    {
                        if (!TypingsFilesEqual(result!.TypingsFiles, project.typingsFiles))
                        {
                            lock (pendingATAChangesMu)
                            {
                                pendingATAChanges[project.configFilePath] = new Snapshot.ATAStateChange
                                {
                                    TypingsInfo = typingsInfo,
                                    TypingsFiles = result.TypingsFiles,
                                    TypingsFilesToWatch = result.FilesToWatch,
                                    Logs = logTree,
                                };
                            }
                            ScheduleDiagnosticsRefresh();
                        }
                    }
                });
            }
        }
    }

    // Go: slices.Equal(result.TypingsFiles, project.typingsFiles).
    private static bool TypingsFilesEqual(string[] a, string[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    // session.go:1761-1837 — warmAutoImportCache.
    private void WarmAutoImportCache(global::Ts.Core.RequestContext ctx, SnapshotChange change, Snapshot oldSnapshot, Snapshot newSnapshot)
    {
        _ = oldSnapshot;
        if (change.FileChanges.Changed.Len() == 1)
        {
            global::Ts.LsProto.DocumentUri changedFile = default;
            // map-iter: 已排序 — single-element set; Go's one-iteration range.
            foreach (global::Ts.LsProto.DocumentUri uri in change.FileChanges.Changed.Keys())
            {
                changedFile = uri;
            }
            if (!newSnapshot.fs.IsOpenFile(changedFile.FileName()))
            {
                return;
            }
            global::Ts.Ls.UserPreferences prefs = newSnapshot.UserPreferences();
            if (prefs.IncludeCompletionsForModuleExports.IsFalse())
            {
                return;
            }
            Project? project = newSnapshot.GetDefaultProject(changedFile);
            if (project is null)
            {
                return;
            }
            if (global::Ts.Ls.AutoImport.AutoImportApi.IsPreparedForImportingFile(
                newSnapshot.AutoImports,
                changedFile.FileName(),
                project.configFilePath,
                prefs))
            {
                return;
            }

            // Cancel any previous auto-import warming and create a new cancellable context.
            // Only publish the new cancel func if the derived context is still active,
            // and make the stored cancel func a no-op once that warming task is done.
            // (Go: context.WithCancel(ctx) → linked CTS; ctx.Err() != nil ⇒
            // IsCancellationRequested.)
            CancellationTokenSource warmCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation);
            global::Ts.Core.RequestContext warmCtx = ctx with { Cancellation = warmCts.Token };
            lock (warmAutoImportMu)
            {
                if (warmAutoImportCancel is not null)
                {
                    warmAutoImportCancel();
                }
                if (!warmCtx.Cancellation.IsCancellationRequested)
                {
                    warmAutoImportCancel = () =>
                    {
                        if (warmCtx.Cancellation.IsCancellationRequested)
                        {
                            return;
                        }
                        logger.Logf("Cancelling auto-import warming for file {0}", changedFile.FileName());
                        warmCts.Cancel();
                    };
                }
            }

            if (warmCtx.Cancellation.IsCancellationRequested)
            {
                warmCts.Cancel();
                return;
            }
            try
            {
                // Clone the snapshot with auto-imports using warmCtx so the expensive
                // extraction work is cancelled if a file change arrives.
                if (!newSnapshot.TryRef())
                {
                    return;
                }

                SnapshotChange warmChange = new SnapshotChange
                {
                    Reason = UpdateReason.RequestedLanguageServiceWithAutoImports,
                    ResourceRequest = new Snapshot.ResourceRequest
                    {
                        Documents = [changedFile],
                        AutoImports = changedFile,
                    },
                };
                Snapshot clonedSnapshot = newSnapshot.Clone(warmCtx, warmChange, newSnapshot.fs.Overlays, this);

                // If cancelled during clone, discard the incomplete result.
                if (warmCtx.Cancellation.IsCancellationRequested)
                {
                    clonedSnapshot.Deref(this);
                    return;
                }

                // Conditionally adopt: if the session hasn't moved past newSnapshot,
                // promote the clone so future requests benefit from the warmed cache.
                AdoptSnapshotChange(newSnapshot, clonedSnapshot);
            }
            finally
            {
                // Go: defer cancel().
                warmCts.Cancel();
                // Go: defer newSnapshot.Deref(s).
                newSnapshot.Deref(this);
            }
        }
    }

    // Api.cs — SessionUpdateLock exposes snapshotUpdateMu (Go same-package field).
    internal Lock SnapshotUpdateLock => snapshotUpdateMu;

    // Go: snapshotID.Add(1) (session.go:325 → Snapshot.Clone calls
    // session.IncrementSnapshotID in the C# port).
    internal ulong IncrementSnapshotID()
    {
        return (ulong)Interlocked.Increment(ref snapshotID);
    }
}

