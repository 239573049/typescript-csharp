// Ported from tsgo internal/api/session.go (lines 31-1127: snapshotData, registries,
// Session core, HandleRequest dispatch, initialize/updateSnapshot/release/parseConfigFile/
// getSourceFile family). Handlers continue in Session.Handlers.cs / Session.CheckerHandlers.cs
// / Session.DiagnosticsHandlers.cs, preserving Go function order.
//
// PORT-NOTE (concurrency): Go's sync.RWMutex → ReaderWriterLockSlim; sync.Mutex → lock;
// atomic.Uint64 → Interlocked. collections.Set[tspath.Path] → Ts.Collections.Set<Path>.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Checker;
using Ts.Collections;
using Ts.LsProto;

namespace Ts.Api;

// session.go:31 — var sessionIDCounter atomic.Uint64.
internal static class SessionIdCounter
{
    // Go: atomic.Uint64.Add — signed Interlocked counter widened to ulong (never negative).
    private static long value;

    public static ulong Add(int delta)
    {
        return (ulong)Interlocked.Add(ref value, delta);
    }
}

// session.go:33-58 — snapshotData holds the per-snapshot state including the snapshot
// itself and symbol/type registries scoped to this snapshot. Multiple clients may hold
// references to the same snapshot via ref counting; the registries are cleaned up when
// refCount reaches zero.
public sealed class SnapshotData
{
    public global::Ts.Project.Snapshot Snapshot = null!;

    public int RefCount;

    // Symbol IDs come from ast.GetSymbolId, a global atomic counter, so the same
    // *ast.Symbol pointer always has the same unique ID across all projects in the
    // snapshot. Symbols are registered snapshot-wide to ensure identity semantics:
    // querying the same symbol from two different projects returns the same handle.
    public Dictionary<SymbolId, global::Ts.Ast.Symbol> SymbolRegistry = [];
    public readonly ReaderWriterLockSlim SymbolRegistryMu = new();

    // symbolCanonicalProjects records, for each registered symbol, the project it was
    // first observed in. Because symbols are shared snapshot-wide (binder symbols are
    // attached to source files, which can be shared across projects), lookups that need
    // a project context (e.g. member/export ordering, node handle resolution) but don't
    // receive one from the caller default to this canonical project. First-writer wins so
    // the choice is stable. Guarded by symbolRegistryMu.
    public Dictionary<SymbolId, ProjectId> SymbolCanonicalProjects = [];

    public Dictionary<ProjectId, ProjectRegistryData> ProjectRegistries = [];
    public readonly ReaderWriterLockSlim ProjectRegistriesMu = new();

    // session.go:72-85 — getProgram looks up a program from a project handle within
    // this snapshot.
    public global::Ts.Compiler.Program GetProgram(ProjectId projectHandle)
    {
        global::Ts.Project.Project proj = GetProject(projectHandle);

        global::Ts.Compiler.Program? program = proj.GetProgram();
        if (program is null)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: project has no program");
        }

        return program;
    }

    // session.go:87-95 — getProject looks up a project from a project handle within
    // this snapshot.
    public global::Ts.Project.Project GetProject(ProjectId projectHandle)
    {
        global::Ts.TsPath.Path projectName = HandleFunctions.ParseProjectHandle(projectHandle);
        global::Ts.Project.Project? proj = Snapshot.ProjectCollection.GetProjectByPath(projectName);
        if (proj is null)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: project {projectName} not found");
        }
        return proj;
    }

    // session.go:97-105 — nodeHandleFrom creates an index-based node handle
    // (index.kind.path), building a node index table for the file on-demand if needed.
    public NodeHandle NodeHandleFrom(global::Ts.Ast.Node node)
    {
        global::Ts.Ast.SourceFile sourceFile = global::Ts.Ast.AstUtilities.GetSourceFileOfNode(node)!;
        global::Ts.TsPath.Path path = sourceFile.Path();
        Ts.Api.Encoder.Encoder.NodeIndexTable table = Ts.Api.Encoder.Encoder.GetNodeIndexTable(sourceFile);
        uint idx = table.GetIndex(node);
        return new NodeHandle(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{idx}.{node.Kind}.{path.Value}"));
    }

    // session.go:107-129 — getOrCreateProjectRegistry returns the registry for the
    // given project, creating it if needed.
    public ProjectRegistryData GetOrCreateProjectRegistry(ProjectId projectID)
    {
        if (projectID.Value == "")
        {
            // Go: panic("getOrCreateProjectRegistry: empty project ID").
            throw new InvalidOperationException("getOrCreateProjectRegistry: empty project ID");
        }
        // Fast path: registry already exists — read lock only.
        ProjectRegistriesMu.EnterReadLock();
        ProjectRegistryData? reg = ProjectRegistries.TryGetValue(projectID, out ProjectRegistryData? found) ? found : null;
        ProjectRegistriesMu.ExitReadLock();
        if (reg is not null)
        {
            return reg;
        }
        // Slow path: create under write lock.
        ProjectRegistriesMu.EnterWriteLock();
        try
        {
            if (!ProjectRegistries.TryGetValue(projectID, out ProjectRegistryData? created) || created is null)
            {
                created = new ProjectRegistryData();
                ProjectRegistries[projectID] = created;
            }
            return created;
        }
        finally
        {
            ProjectRegistriesMu.ExitWriteLock();
        }
    }

    // session.go:131-169 — newSymbolResponse registers a symbol in the snapshot's
    // registry and returns the response. canonicalProject is the project the symbol was
    // observed in and must be non-empty; it is recorded as the symbol's canonical project
    // (first writer wins) and returned to the client so it can default project-scoped
    // follow-up lookups (members/exports, node resolution) to it.
    public SymbolResponse? NewSymbolResponse(global::Ts.Ast.Symbol? symbol, ProjectId canonicalProject)
    {
        if (symbol is null)
        {
            return null;
        }

        (SymbolId id, ProjectId project) = RegisterSymbol(symbol, canonicalProject);
        var resp = new SymbolResponse
        {
            Id = id,
            Project = project,
            Name = global::Ts.Ast.AstUtilities.EscapeSymbolName(symbol.Name),
            Flags = (uint)symbol.Flags,
            CheckFlags = (uint)symbol.CheckFlags,
        };

        if (symbol.Declarations is { Length: > 0 } declarations)
        {
            resp.Declarations = new NodeHandle[declarations.Length];
            for (int i = 0; i < declarations.Length; i++)
            {
                resp.Declarations[i] = NodeHandleFrom(declarations[i]);
            }
        }

        if (symbol.ValueDeclaration is not null)
        {
            resp.ValueDeclaration = NodeHandleFrom(symbol.ValueDeclaration);
        }

        if (symbol.Parent is not null)
        {
            resp.Parent = HandleFunctions.SymbolHandle(symbol.Parent);
        }

        if (symbol.ExportSymbol is not null)
        {
            resp.ExportSymbol = HandleFunctions.SymbolHandle(symbol.ExportSymbol);
        }

        return resp;
    }

    // session.go:171-200 — registerSymbol registers a symbol in the snapshot's registry
    // and returns its handle along with its canonical project. The canonical project is
    // the project the symbol was first observed in (first writer wins for stability) and
    // is always non-empty: every symbol handed to a client must carry a project so that
    // project-scoped follow-up lookups (members/exports, parent, node resolution) have a
    // default context. Callers must supply a non-empty project.
    public (SymbolId Id, ProjectId Project) RegisterSymbol(global::Ts.Ast.Symbol? symbol, ProjectId canonicalProject)
    {
        if (symbol is null)
        {
            return (default, default);
        }
        if (canonicalProject.Value == "")
        {
            // Go: panic("registerSymbol requires a non-empty canonical project").
            throw new InvalidOperationException("registerSymbol requires a non-empty canonical project");
        }
        SymbolId id = HandleFunctions.SymbolHandle(symbol);
        SymbolRegistryMu.EnterWriteLock();
        try
        {
            SymbolRegistry.TryGetValue(id, out global::Ts.Ast.Symbol? existing);
            if (existing is not null)
            {
                if (!ReferenceEquals(existing, symbol))
                {
                    // Go: panic("duplicate symbol").
                    throw new InvalidOperationException("duplicate symbol");
                }
            }
            else
            {
                SymbolRegistry[id] = symbol;
            }
            if (!SymbolCanonicalProjects.TryGetValue(id, out ProjectId project))
            {
                SymbolCanonicalProjects[id] = canonicalProject;
                project = canonicalProject;
            }
            return (id, project);
        }
        finally
        {
            SymbolRegistryMu.ExitWriteLock();
        }
    }

    // session.go:202-208 — newTypeResponse registers a type in the project's registry
    // and returns the response.
    public TypeResponse? NewTypeResponse(ProjectId projectID, global::Ts.Checker.Type? t)
    {
        if (t is null)
        {
            return null;
        }
        return ProtoFunctions.NewTypeResponse(t, RegisterType(projectID, t));
    }

    // session.go:210-228.
    public ApiTypeId RegisterType(ProjectId projectID, global::Ts.Checker.Type? t)
    {
        if (t is null)
        {
            return default;
        }
        ApiTypeId id = HandleFunctions.TypeHandle(t);
        ProjectRegistryData reg = GetOrCreateProjectRegistry(projectID);
        reg.TypeRegistryMu.EnterWriteLock();
        try
        {
            reg.TypeRegistry.TryGetValue(id, out global::Ts.Checker.Type? existing);

            if (existing is not null)
            {
                if (!ReferenceEquals(existing, t))
                {
                    // Go: panic("duplicate type").
                    throw new InvalidOperationException("duplicate type");
                }
                return id;
            }
            reg.TypeRegistry[id] = t;
            return id;
        }
        finally
        {
            reg.TypeRegistryMu.ExitWriteLock();
        }
    }

    // session.go:230-245 — resolveSymbolHandle resolves a symbol handle within the
    // snapshot's registry.
    public global::Ts.Ast.Symbol? ResolveSymbolHandle(SymbolId handle)
    {
        if (handle.Value == 0)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: empty symbol handle");
        }

        SymbolRegistryMu.EnterReadLock();
        SymbolRegistry.TryGetValue(handle, out global::Ts.Ast.Symbol? symbol);
        SymbolRegistryMu.ExitReadLock();

        if (symbol is null)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: symbol handle {handle.Value} not found in snapshot registry");
        }

        return symbol;
    }

    // session.go:247-273 — resolveTypeHandle resolves a type handle within the
    // project's registry.
    public global::Ts.Checker.Type? ResolveTypeHandle(ProjectId projectID, ApiTypeId handle)
    {
        if (handle.Value == 0)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: empty type handle");
        }
        if (projectID.Value == "")
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: empty project ID for type handle {handle.Value}");
        }

        ProjectRegistriesMu.EnterReadLock();
        ProjectRegistries.TryGetValue(projectID, out ProjectRegistryData? reg);
        ProjectRegistriesMu.ExitReadLock();

        if (reg is null)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: type handle {handle.Value} not found (no registry for project {projectID.Value})");
        }

        reg.TypeRegistryMu.EnterReadLock();
        reg.TypeRegistry.TryGetValue(handle, out global::Ts.Checker.Type? t);
        reg.TypeRegistryMu.ExitReadLock();

        if (t is null)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: type handle {handle.Value} not found in project registry");
        }

        return t;
    }

    // session.go:275-301 — resolveSignatureHandle resolves a signature handle within
    // the project's registry.
    public Signature? ResolveSignatureHandle(ProjectId projectID, ApiSignatureId handle)
    {
        if (handle.Value == 0)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: empty signature handle");
        }
        if (projectID.Value == "")
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: empty project ID for signature handle {handle.Value}");
        }

        ProjectRegistriesMu.EnterReadLock();
        ProjectRegistries.TryGetValue(projectID, out ProjectRegistryData? reg);
        ProjectRegistriesMu.ExitReadLock();

        if (reg is null)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: signature handle {handle.Value} not found (no registry for project {projectID.Value})");
        }

        reg.SignatureRegistryMu.EnterReadLock();
        reg.SignatureRegistry.TryGetValue(handle, out Signature? sig);
        reg.SignatureRegistryMu.ExitReadLock();

        if (sig is null)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: signature handle {handle.Value} not found in project registry");
        }

        return sig;
    }

    // session.go:303-334 — newSignatureResponse registers a signature in the project's
    // registry and returns the response.
    public SignatureResponse? NewSignatureResponse(ProjectId projectID, Signature? sig)
    {
        if (sig is null)
        {
            return null;
        }
        var resp = new SignatureResponse
        {
            Id = RegisterSignature(projectID, sig),
            Flags = (uint)sig.Flags(),
        };

        if (sig.Declaration() is not null)
        {
            resp.Declaration = NodeHandleFrom(sig.Declaration()!);
        }

        if (sig.TypeParameters() is { Length: > 0 } typeParameters)
        {
            resp.TypeParameters = ProtoFunctions.TypeHandles(typeParameters);
        }

        if (sig.Parameters() is { Length: > 0 } parameters)
        {
            resp.Parameters = ProtoFunctions.SymbolHandles(parameters);
        }

        if (sig.ThisParameter() is not null)
        {
            resp.ThisParameter = HandleFunctions.SymbolHandle(sig.ThisParameter()!);
        }

        if (sig.Target() is not null)
        {
            resp.Target = HandleFunctions.SignatureHandle(sig.Target()!);
        }

        return resp;
    }

    // session.go:336-354.
    public ApiSignatureId RegisterSignature(ProjectId projectID, Signature? sig)
    {
        if (sig is null)
        {
            return default;
        }
        ApiSignatureId id = HandleFunctions.SignatureHandle(sig);
        ProjectRegistryData reg = GetOrCreateProjectRegistry(projectID);
        reg.SignatureRegistryMu.EnterWriteLock();
        try
        {
            reg.SignatureRegistry.TryGetValue(id, out Signature? existing);

            if (existing is not null)
            {
                if (!ReferenceEquals(existing, sig))
                {
                    // Go: panic("duplicate signature").
                    throw new InvalidOperationException("duplicate signature");
                }
                return id;
            }
            reg.SignatureRegistry[id] = sig;
            return id;
        }
        finally
        {
            reg.SignatureRegistryMu.ExitWriteLock();
        }
    }

    // session.go:2677-2709 — resolveNodeHandle resolves a node handle to a node within
    // the snapshot's programs.
    public global::Ts.Ast.Node? ResolveNodeHandle(global::Ts.Compiler.Program program, NodeHandle handle)
    {
        string s = handle.Value ?? "";
        // Format: "index.kind.path" — we need index and path, kind is informational only.
        int firstDot = s.IndexOf('.');
        if (firstDot == -1)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: invalid node handle \"{handle.Value}\"");
        }
        int secondDot = s.IndexOf('.', firstDot + 1);
        if (secondDot == -1)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: invalid node handle \"{handle.Value}\"");
        }

        if (!ulong.TryParse(s[..firstDot], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong idx))
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: invalid node handle \"{handle.Value}\"");
        }
        global::Ts.TsPath.Path path = new(s[(secondDot + 1)..]);

        global::Ts.Ast.SourceFile? sourceFile = program.GetSourceFileByPath(path);
        if (sourceFile is null)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: node handle \"{handle.Value}\" could not be resolved (file may not be loaded or handle may be stale)");
        }
        Ts.Api.Encoder.Encoder.NodeIndexTable table = Ts.Api.Encoder.Encoder.GetNodeIndexTable(sourceFile);

        // Go: if table != nil && idx < uint64(len(table.Nodes)); the C# GetNodeIndexTable
        // never returns nil, so only the bounds check is needed.
        if (idx < (ulong)table.Nodes.Length)
        {
            global::Ts.Ast.Node? node = table.Nodes[idx];
            if (node != null)
            {
                return node;
            }
        }
        throw new ApiRequestException($"{ProtoErrors.ErrClientError}: node handle \"{handle.Value}\" could not be resolved (file may not be loaded or handle may be stale)");
    }
}

// session.go:60-70 — projectRegistryData holds per-project type and signature
// registries. Types and signatures use per-checker sequential IDs, so the same local ID
// can appear in multiple projects. Separate maps per project prevent collisions
// and allow clean teardown when a project is removed.
public sealed class ProjectRegistryData
{
    public Dictionary<ApiTypeId, global::Ts.Checker.Type> TypeRegistry = [];
    public readonly ReaderWriterLockSlim TypeRegistryMu = new();

    public Dictionary<ApiSignatureId, Signature> SignatureRegistry = [];
    public readonly ReaderWriterLockSlim SignatureRegistryMu = new();
}

// Session represents an API session that provides programmatic access
// to TypeScript language services through the LSP server.
// It implements the Handler interface to process incoming API requests.
// The session supports multiple active snapshots, each with their own
// symbol and type registries for maintaining object identity.
// (session.go:356-404)
public sealed partial class Session : Handler
{
    private readonly string id;
    private readonly global::Ts.Project.Session projectSession;

    // This is set to true when using MessagePackProtocol. Not readonly: Go's newSession
    // assigns it on the constructed value (session.go:421-425), mirrored by NewSession.
    private bool useBinaryResponses;

    // snapshots maps snapshot handles to their data. Each snapshot has its own
    // symbol/type registries.
    //
    // snapshotsMu guards the snapshots map and latestSnapshot. It is held only for
    // short, map-bounded critical sections, never across slow work like a project
    // snapshot update or checker queries. Read handlers (getSnapshotData and the
    // language-service handlers built on it) take it for reading; handleRelease and
    // the bookkeeping tail of handleUpdateSnapshot take it for writing. This is what
    // lets queries against an existing snapshot run concurrently with the building of
    // the next one.
    private readonly Dictionary<SnapshotId, SnapshotData> snapshots = [];
    private readonly ReaderWriterLockSlim snapshotsMu = new();

    // latestSnapshot tracks the most recently created snapshot, used as the diff base
    // for the next update. Guarded by snapshotsMu.
    private SnapshotId latestSnapshot;

    // openProjects and openFiles track the projects and files this session
    // currently holds open in the project session's API state. The session holds
    // at most one ref per project/file (opens are idempotent), so it can release
    // exactly those refs on Close and never send a close for a ref it doesn't hold.
    // Guarded by updateMu.
    private readonly global::Ts.Collections.Set<global::Ts.TsPath.Path> openProjects = new();
    private readonly global::Ts.Collections.Set<global::Ts.TsPath.Path> openFiles = new();

    // updateMu serializes the whole of handleUpdateSnapshot (and releaseOpenRefs)
    // against other updates. Unlike snapshotsMu it is held across the slow
    // projectSession.APIUpdate call, because building the request from
    // openProjects/openFiles, applying it, committing the ref tracking, and advancing
    // latestSnapshot must be one atomic step; otherwise concurrent updates could
    // double-count refs or diff against a non-adjacent snapshot. Read handlers do NOT
    // take this lock, so an in-flight update never blocks queries against existing
    // snapshots. Lock ordering is updateMu -> snapshotsMu (never the reverse).
    private readonly object updateMu = new();

    private readonly global::Ts.Profiling.CpuProfiler cpuProfiler = new();

    // SessionOptions configures an API session. (session.go:409-413)
    public sealed class SessionOptions
    {
        // UseBinaryResponses enables binary responses for msgpack protocol.
        public bool UseBinaryResponses;
    }

    // NewSession creates a new API session with the given project session.
    // (session.go:415-427) PORT-NOTE: Go's `NewSession` is a package function; C# uses
    // a static factory (the constructor is private to mirror the Go constructor shape).
    public static Session NewSession(global::Ts.Project.Session projectSession, SessionOptions? options)
    {
        ulong id = SessionIdCounter.Add(1);
        var s = new Session(id, projectSession);
        if (options is not null)
        {
            s.useBinaryResponses = options.UseBinaryResponses;
        }
        return s;
    }

    private Session(ulong counterId, global::Ts.Project.Session projectSession)
    {
        id = FormatSessionId(counterId);
        this.projectSession = projectSession;
    }

    // ID returns the unique identifier for this session. (session.go:429-432)
    public string Id()
    {
        return id;
    }

    // ProjectSession returns the underlying project session. (session.go:434-437)
    public global::Ts.Project.Session ProjectSession()
    {
        return projectSession;
    }

    // snapshotHandle creates a snapshot handle from a snapshot's ID. (session.go:439-442)
    private static SnapshotId SnapshotHandle(global::Ts.Project.Snapshot snapshot)
    {
        return new SnapshotId(snapshot.ID());
    }

    // getSnapshotData looks up snapshot data by handle. (session.go:444-453)
    private SnapshotData GetSnapshotData(SnapshotId handle)
    {
        snapshotsMu.EnterReadLock();
        bool exists = snapshots.TryGetValue(handle, out SnapshotData? sd);
        snapshotsMu.ExitReadLock();
        if (!exists)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: snapshot {handle.Value} not found");
        }
        return sd!;
    }

    // session.go:455-486 — checkerSetup holds the common context needed by handlers
    // that require a type checker.
    private readonly struct CheckerSetup(
        SnapshotData sd,
        global::Ts.Compiler.Program program,
        global::Ts.Checker.Checker checker,
        Action done,
        ProjectId projectId)
    {
        public SnapshotData Sd { get; } = sd;
        public global::Ts.Compiler.Program Program { get; } = program;
        public global::Ts.Checker.Checker Checker { get; } = checker;
        public Action Done { get; } = done;
        public ProjectId ProjectId { get; } = projectId;

        public TypeResponse? NewTypeResponse(global::Ts.Checker.Type t)
        {
            return Sd.NewTypeResponse(ProjectId, t);
        }

        public SymbolResponse? NewSymbolResponse(global::Ts.Ast.Symbol? sym)
        {
            return Sd.NewSymbolResponse(sym, ProjectId);
        }

        public SignatureResponse? NewSignatureResponse(Signature sig)
        {
            return Sd.NewSignatureResponse(ProjectId, sig);
        }

        public global::Ts.Checker.Type? ResolveTypeHandle(ApiTypeId id)
        {
            return Sd.ResolveTypeHandle(ProjectId, id);
        }

        public global::Ts.Ast.Symbol? ResolveSymbolHandle(SymbolId id)
        {
            return Sd.ResolveSymbolHandle(id);
        }

        public Signature? ResolveSignatureHandle(ApiSignatureId id)
        {
            return Sd.ResolveSignatureHandle(ProjectId, id);
        }

        // resolveNodeHandle's err path (session.go:1353-1355 and every node-taking
        // handler): Go returns the error to the client; the C# port throws inside
        // ResolveNodeHandle, so this helper collapses the null branch into that throw.
        public global::Ts.Ast.Node ResolveNode(NodeHandle handle)
        {
            global::Ts.Ast.Node? node = Sd.ResolveNodeHandle(Program, handle);
            if (node is null)
            {
                throw new ApiRequestException($"{ProtoErrors.ErrClientError}: node handle \"{handle.Value}\" could not be resolved");
            }
            return node;
        }
    }

    // setupChecker resolves snapshot, program, and type checker for a project.
    // Callers must defer setup.done() to release the checker. (session.go:488-509)
    private CheckerSetup SetupChecker(global::Ts.Core.RequestContext ctx, SnapshotId snapshot, ProjectId projectHandle)
    {
        SnapshotData sd = GetSnapshotData(snapshot);

        global::Ts.Compiler.Program program = sd.GetProgram(projectHandle);

        // Go: program.GetTypeChecker(ctx) with the Api lifetime stamped on the context
        // (session.go:493-495). The C# Program.GetTypeChecker takes the cancellation
        // token only (Compiler/Program.cs:256); lifetime-aware pool acquisition lands
        // with the checker-pool wave, so the stamped RequestContext stays on ctx.
        ctx = ctx.WithCheckerLifetime(global::Ts.Core.CheckerLifetime.Api);
        (global::Ts.Checker.Checker c, Action done) = program.GetTypeChecker(ctx.Cancellation);
        return new CheckerSetup(sd, program, c, done, projectHandle);
    }

    // setupLanguageService creates a LanguageService for the given snapshot/project.
    // Unlike setupChecker, this does NOT acquire a checker from the pool, so callers that
    // only need an LS (and not a Checker) can avoid blocking on / holding a pooled checker.
    //
    // The LS acquires its own checker internally (keyed by the ctx's checker lifetime).
    // If a handler returns symbol/type/signature handles the client may later re-query
    // on the API checker (e.g. completion with IncludeSymbol -> GetTypeOfSymbol), wrap
    // ctx with core.WithCheckerLifetime(ctx, core.CheckerLifetimeAPI) so those handles
    // are produced on the persistent API checker and stay resolvable. Only safe when the
    // LS operation acquires a checker exactly once; nested acquisitions (e.g. find-all-
    // references) would deadlock on the single-slot persistent checker.
    // (session.go:511-529)
    //
    // PORT-NOTE(api): Go returns ls.NewLanguageService(proj.ID(), program, sd.snapshot,
    // activeFile) — an LS over the project session's snapshot with project-scoped
    // preferences / auto-import / activeFile state. The C# foundation Ls.Host
    // (Ls/Host.cs) carries only the program/checker surface: none of those four inputs
    // have a consumer there yet (ProgramHost.AutoImportRegistry already returns null
    // pending the project wave), and every Api call site passes activeFile == "".
    // The handlers driven through this seam (signature usages, completions, referenced
    // symbols) are program-scoped queries, so the LS is built over the same program via
    // ProgramHost — the identical object the static Ts.Ls.LanguageService factory wraps.
    // Revisit when the project-system host lands (registered deviation, api wave report).
    private global::Ts.Ls.LanguageService? SetupLanguageService(SnapshotData sd, global::Ts.Compiler.Program program, ProjectId projectHandle, string activeFile)
    {
        return new global::Ts.Ls.LanguageService(new global::Ts.Ls.ProgramHost(program));
    }

    // HandleRequest implements Handler. (session.go:531-780)
    public object? HandleRequest(global::Ts.Core.RequestContext ctx, string method, byte[]? @params)
    {
        // Handle simple methods that don't need param parsing
        switch (method)
        {
            case "echo":
                // Return raw binary for msgpack protocol compatibility
                if (useBinaryResponses)
                {
                    return new RawBinary(@params ?? []);
                }
                return @params;
            case "ping":
                return "pong";
        }

        object? parsed;
        try
        {
            parsed = ProtoDispatch.UnmarshalPayload(method, @params);
        }
        catch (ApiRequestException)
        {
            throw;
        }
        catch (Exception err)
        {
            // Go: fmt.Errorf("%w: %w", ErrInvalidRequest, err).
            throw new ApiRequestException($"{ProtoErrors.ErrInvalidRequest}: {err.Message}");
        }

        switch (method)
        {
            case "release":
                return HandleRequestResult(HandleRelease(ctx, (ReleaseParams)parsed!));
            case "initialize":
                return HandleRequestResult(HandleInitialize(ctx));
            case "updateSnapshot":
                return HandleRequestResult(HandleUpdateSnapshot(ctx, (UpdateSnapshotParams)parsed!));
            case "parseConfigFile":
                return HandleRequestResult(HandleParseConfigFile(ctx, (ParseConfigFileParams)parsed!));
            case "getDefaultProjectForFile":
                return HandleRequestResult(HandleGetDefaultProjectForFile(ctx, (GetDefaultProjectForFileParams)parsed!));
            case "getSourceFile":
                return HandleRequestResult(HandleGetSourceFile(ctx, (GetSourceFileParams)parsed!));
            case "getSourceFileNames":
                return HandleRequestResult(HandleGetSourceFileNames(ctx, (GetSourceFileNamesParams)parsed!));
            case "getSourceFileMetadata":
                return HandleRequestResult(HandleGetSourceFileMetadata(ctx, (GetSourceFileParams)parsed!));
            case "getSymbolAtPosition":
                return HandleRequestResult(HandleGetSymbolAtPosition(ctx, (GetSymbolAtPositionParams)parsed!));
            case "getSymbolsAtPositions":
                return HandleRequestResult(HandleGetSymbolsAtPositions(ctx, (GetSymbolsAtPositionsParams)parsed!));
            case "getSymbolAtLocation":
                return HandleRequestResult(HandleGetSymbolAtLocation(ctx, (GetSymbolAtLocationParams)parsed!));
            case "getSymbolsAtLocations":
                return HandleRequestResult(HandleGetSymbolsAtLocations(ctx, (GetSymbolsAtLocationsParams)parsed!));
            case "getTypeOfSymbol":
                return HandleRequestResult(HandleGetTypeOfSymbol(ctx, (GetTypeOfSymbolParams)parsed!));
            case "getTypesOfSymbols":
                return HandleRequestResult(HandleGetTypesOfSymbols(ctx, (GetTypesOfSymbolsParams)parsed!));
            case "getDeclaredTypeOfSymbol":
                return HandleRequestResult(HandleGetDeclaredTypeOfSymbol(ctx, (GetTypeOfSymbolParams)parsed!));
            case "resolveName":
                return HandleRequestResult(HandleResolveName(ctx, (ResolveNameParams)parsed!));
            case "getSignaturesOfType":
                return HandleRequestResult(HandleGetSignaturesOfType(ctx, (GetSignaturesOfTypeParams)parsed!));
            case "getResolvedSignature":
                return HandleRequestResult(HandleGetResolvedSignature(ctx, (GetResolvedSignatureParams)parsed!));
            case "getTypeAtLocation":
                return HandleRequestResult(HandleGetTypeAtLocation(ctx, (GetTypeAtLocationParams)parsed!));
            case "getTypeAtLocations":
                return HandleRequestResult(HandleGetTypesAtLocations(ctx, (GetTypeAtLocationsParams)parsed!));
            case "getTypeAtPosition":
                return HandleRequestResult(HandleGetTypeAtPosition(ctx, (GetTypeAtPositionParams)parsed!));
            case "getTypesAtPositions":
                return HandleRequestResult(HandleGetTypesAtPositions(ctx, (GetTypesAtPositionsParams)parsed!));
            case "getParentOfSymbol":
                return HandleRequestResult(HandleGetParentOfSymbol(ctx, (GetSymbolPropertyParams)parsed!));
            case "getMembersOfSymbol":
                return HandleRequestResult(HandleGetMembersOfSymbol(ctx, (GetSymbolPropertyParams)parsed!));
            case "getExportsOfSymbol":
                return HandleRequestResult(HandleGetExportsOfSymbol(ctx, (GetSymbolPropertyParams)parsed!));
            case "getExportSymbolOfSymbol":
                return HandleRequestResult(HandleGetExportSymbolOfSymbol(ctx, (GetSymbolPropertyParams)parsed!));
            case "getSymbolOfType":
                return HandleRequestResult(HandleGetSymbolOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getTargetOfType":
                return HandleRequestResult(HandleGetTargetOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getFreshTypeOfType":
                return HandleRequestResult(HandleGetFreshTypeOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getRegularTypeOfType":
                return HandleRequestResult(HandleGetRegularTypeOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getTypesOfType":
                return HandleRequestResult(HandleGetTypesOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getTypeParametersOfType":
                return HandleRequestResult(HandleGetTypeParametersOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getOuterTypeParametersOfType":
                return HandleRequestResult(HandleGetOuterTypeParametersOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getLocalTypeParametersOfType":
                return HandleRequestResult(HandleGetLocalTypeParametersOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getAliasTypeArgumentsOfType":
                return HandleRequestResult(HandleGetAliasTypeArgumentsOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getAliasSymbolOfType":
                return HandleRequestResult(HandleGetAliasSymbolOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getObjectTypeOfType":
                return HandleRequestResult(HandleGetObjectTypeOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getIndexTypeOfType":
                return HandleRequestResult(HandleGetIndexTypeOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getCheckTypeOfType":
                return HandleRequestResult(HandleGetCheckTypeOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getExtendsTypeOfType":
                return HandleRequestResult(HandleGetExtendsTypeOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getBaseTypeOfType":
                return HandleRequestResult(HandleGetBaseTypeOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getConstraintOfType":
                return HandleRequestResult(HandleGetConstraintOfType(ctx, (GetTypePropertyParams)parsed!));
            case "getTrueTypeOfConditionalType":
                return HandleRequestResult(HandleGetTrueTypeOfConditionalType(ctx, (GetTypePropertyParams)parsed!));
            case "getFalseTypeOfConditionalType":
                return HandleRequestResult(HandleGetFalseTypeOfConditionalType(ctx, (GetTypePropertyParams)parsed!));
            case "getTypeParametersOfSignature":
                return HandleRequestResult(HandleGetTypeParametersOfSignature(ctx, (GetSignaturePropertyParams)parsed!));
            case "getParametersOfSignature":
                return HandleRequestResult(HandleGetParametersOfSignature(ctx, (GetSignaturePropertyParams)parsed!));
            case "getThisParameterOfSignature":
                return HandleRequestResult(HandleGetThisParameterOfSignature(ctx, (GetSignaturePropertyParams)parsed!));
            case "getTargetOfSignature":
                return HandleRequestResult(HandleGetTargetOfSignature(ctx, (GetSignaturePropertyParams)parsed!));
            case "getContextualType":
                return HandleRequestResult(HandleGetContextualType(ctx, (GetContextualTypeParams)parsed!));
            case "getBaseTypeOfLiteralType":
                return HandleRequestResult(HandleGetBaseTypeOfLiteralType(ctx, (GetBaseTypeOfLiteralTypeParams)parsed!));
            case "getNonNullableType":
                return HandleRequestResult(HandleGetNonNullableType(ctx, (GetNonNullableTypeParams)parsed!));
            case "getTypeFromTypeNode":
                return HandleRequestResult(HandleGetTypeFromTypeNode(ctx, (GetTypeFromTypeNodeParams)parsed!));
            case "getWidenedType":
                return HandleRequestResult(HandleGetWidenedType(ctx, (GetWidenedTypeParams)parsed!));
            case "getParameterType":
                return HandleRequestResult(HandleGetParameterType(ctx, (GetParameterTypeParams)parsed!));
            case "isArrayLikeType":
                return HandleRequestResult(HandleIsArrayLikeType(ctx, (IsArrayLikeTypeParams)parsed!));
            case "isTypeAssignableTo":
                return HandleRequestResult(HandleIsTypeAssignableTo(ctx, (IsTypeAssignableToParams)parsed!));
            case "getShorthandAssignmentValueSymbol":
                return HandleRequestResult(HandleGetShorthandAssignmentValueSymbol(ctx, (GetTypeAtLocationParams)parsed!));
            case "getTypeOfSymbolAtLocation":
                return HandleRequestResult(HandleGetTypeOfSymbolAtLocation(ctx, (GetTypeOfSymbolAtLocationParams)parsed!));
            case "typeToTypeNode":
                return HandleRequestResult(HandleTypeToTypeNode(ctx, (TypeToTypeNodeParams)parsed!));
            case "signatureToSignatureDeclaration":
                return HandleRequestResult(HandleSignatureToSignatureDeclaration(ctx, (SignatureToSignatureDeclarationParams)parsed!));
            case "typeToString":
                return HandleRequestResult(HandleTypeToString(ctx, (TypeToTypeNodeParams)parsed!));
            case "printNode":
                return HandleRequestResult(HandlePrintNode(ctx, (PrintNodeParams)parsed!));
            case "isContextSensitive":
                return HandleRequestResult(HandleIsContextSensitive(ctx, (GetContextualTypeParams)parsed!));
            case "getReturnTypeOfSignature":
                return HandleRequestResult(HandleGetReturnTypeOfSignature(ctx, (CheckerSignatureParams)parsed!));
            case "getRestTypeOfSignature":
                return HandleRequestResult(HandleGetRestTypeOfSignature(ctx, (CheckerSignatureParams)parsed!));
            case "getTypePredicateOfSignature":
                return HandleRequestResult(HandleGetTypePredicateOfSignature(ctx, (CheckerSignatureParams)parsed!));
            case "getBaseTypes":
                return HandleRequestResult(HandleGetBaseTypes(ctx, (CheckerTypeParams)parsed!));
            case "getPropertiesOfType":
                return HandleRequestResult(HandleGetPropertiesOfType(ctx, (CheckerTypeParams)parsed!));
            case "getApparentType":
                return HandleRequestResult(HandleGetApparentType(ctx, (CheckerTypeParams)parsed!));
            case "getPropertyOfType":
                return HandleRequestResult(HandleGetPropertyOfType(ctx, (GetPropertyOfTypeParams)parsed!));
            case "getIndexInfosOfType":
                return HandleRequestResult(HandleGetIndexInfosOfType(ctx, (CheckerTypeParams)parsed!));
            case "getConstraintOfTypeParameter":
                return HandleRequestResult(HandleGetConstraintOfTypeParameter(ctx, (CheckerTypeParams)parsed!));
            case "getBaseConstraintOfType":
                return HandleRequestResult(HandleGetBaseConstraintOfType(ctx, (CheckerTypeParams)parsed!));
            case "getTypeArguments":
                return HandleRequestResult(HandleGetTypeArguments(ctx, (CheckerTypeParams)parsed!));
            case "getConstantValue":
                return HandleRequestResult(HandleGetConstantValue(ctx, (CheckerNodeParams)parsed!));
            case "getSignatureFromDeclaration":
                return HandleRequestResult(HandleGetSignatureFromDeclaration(ctx, (CheckerNodeParams)parsed!));
            case "getExportSpecifierLocalTargetSymbol":
                return HandleRequestResult(HandleGetExportSpecifierLocalTargetSymbol(ctx, (CheckerNodeParams)parsed!));
            case "getAliasedSymbol":
                return HandleRequestResult(HandleGetAliasedSymbol(ctx, (CheckerSymbolParams)parsed!));
            case "getImmediateAliasedSymbol":
                return HandleRequestResult(HandleGetImmediateAliasedSymbol(ctx, (CheckerSymbolParams)parsed!));
            case "getExportsOfModule":
                return HandleRequestResult(HandleGetExportsOfModule(ctx, (CheckerSymbolParams)parsed!));
            case "getMemberInModuleExports":
                return HandleRequestResult(HandleGetMemberInModuleExports(ctx, (GetMemberInModuleExportsParams)parsed!));
            case "getJsDocTags":
                return HandleRequestResult(HandleGetJSDocTags(ctx, (CheckerSymbolParams)parsed!));
            case "getDocumentationComment":
                return HandleRequestResult(HandleGetDocumentationComment(ctx, (CheckerSymbolParams)parsed!));
            case "isArrayType":
                return HandleRequestResult(HandleIsArrayType(ctx, (CheckerTypeParams)parsed!));
            case "isTupleType":
                return HandleRequestResult(HandleIsTupleType(ctx, (CheckerTypeParams)parsed!));
            case "getAnyType":
                return HandleRequestResult(HandleGetAnyType(ctx, (GetIntrinsicTypeParams)parsed!));
            case "getStringType":
                return HandleRequestResult(HandleGetIntrinsicType(ctx, (GetIntrinsicTypeParams)parsed!, static c => c.GetStringType()));
            case "getNumberType":
                return HandleRequestResult(HandleGetIntrinsicType(ctx, (GetIntrinsicTypeParams)parsed!, static c => c.GetNumberType()));
            case "getBooleanType":
                return HandleRequestResult(HandleGetBooleanType(ctx, (GetIntrinsicTypeParams)parsed!));
            case "getVoidType":
                return HandleRequestResult(HandleGetVoidType(ctx, (GetIntrinsicTypeParams)parsed!));
            case "getUndefinedType":
                return HandleRequestResult(HandleGetIntrinsicType(ctx, (GetIntrinsicTypeParams)parsed!, static c => c.GetUndefinedType()));
            case "getNullType":
                return HandleRequestResult(HandleGetNullType(ctx, (GetIntrinsicTypeParams)parsed!));
            case "getNeverType":
                return HandleRequestResult(HandleGetNeverType(ctx, (GetIntrinsicTypeParams)parsed!));
            case "getUnknownType":
                return HandleRequestResult(HandleGetUnknownType(ctx, (GetIntrinsicTypeParams)parsed!));
            case "getBigIntType":
                return HandleRequestResult(HandleGetBigIntType(ctx, (GetIntrinsicTypeParams)parsed!));
            case "getESSymbolType":
                return HandleRequestResult(HandleGetESSymbolType(ctx, (GetIntrinsicTypeParams)parsed!));
            case "getWellKnownSymbols":
                return HandleRequestResult(HandleGetWellKnownSymbols(ctx, (GetIntrinsicTypeParams)parsed!));
            case "getWellKnownSignatures":
                return HandleRequestResult(HandleGetWellKnownSignatures(ctx, (GetIntrinsicTypeParams)parsed!));
            case "getSyntacticDiagnostics":
                return HandleRequestResult(HandleGetSyntacticDiagnostics(ctx, (GetDiagnosticsParams)parsed!));
            case "getBindDiagnostics":
                return HandleRequestResult(HandleGetBindDiagnostics(ctx, (GetDiagnosticsParams)parsed!));
            case "getSemanticDiagnostics":
                return HandleRequestResult(HandleGetSemanticDiagnostics(ctx, (GetDiagnosticsParams)parsed!));
            case "getSuggestionDiagnostics":
                return HandleRequestResult(HandleGetSuggestionDiagnostics(ctx, (GetDiagnosticsParams)parsed!));
            case "getDeclarationDiagnostics":
                return HandleRequestResult(HandleGetDeclarationDiagnostics(ctx, (GetDiagnosticsParams)parsed!));
            case "getProgramDiagnostics":
                return HandleRequestResult(HandleGetProgramDiagnostics(ctx, (GetProjectDiagnosticsParams)parsed!));
            case "getGlobalDiagnostics":
                return HandleRequestResult(HandleGetGlobalDiagnostics(ctx, (GetProjectDiagnosticsParams)parsed!));
            case "getConfigFileParsingDiagnostics":
                return HandleRequestResult(HandleGetConfigFileParsingDiagnostics(ctx, (GetProjectDiagnosticsParams)parsed!));
            case "startCPUProfile":
                return HandleRequestResult(HandleStartCPUProfile(ctx, (ProfileParams)parsed!));
            case "stopCPUProfile":
                return HandleRequestResult(HandleStopCPUProfile(ctx));
            case "saveHeapProfile":
                return HandleRequestResult(HandleSaveHeapProfile(ctx, (ProfileParams)parsed!));
            case "getReferencesToSymbolInFile":
                return HandleRequestResult(HandleGetReferencesToSymbolInFile(ctx, (GetReferencesToSymbolInFileParams)parsed!));
            case "getReferencedSymbolsForNode":
                return HandleRequestResult(HandleGetReferencedSymbolsForNode(ctx, (GetReferencedSymbolsForNodeParams)parsed!));
            case "getSignatureUsages":
                return HandleRequestResult(HandleGetSignatureUsages(ctx, (GetSignatureUsagesParams)parsed!));
            case "getCompletionsAtPosition":
                return HandleRequestResult(HandleGetCompletionsAtPosition(ctx, (GetCompletionsAtPositionParams)parsed!));
            default:
                throw new ApiRequestException($"unknown method: {method}");
        }
    }

    // Go's `(result, nil)` handler returns flow through HandleRequest; the helper name
    // documents the tail end of the dispatch (the C# handlers return values directly).
    private static object? HandleRequestResult(object? result)
    {
        return result;
    }

    // session.go:782-790.
    private object? HandleStartCPUProfile(global::Ts.Core.RequestContext ctx, ProfileParams @params)
    {
        if (@params is null || @params.Dir == "")
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: dir is required");
        }
        string? err = cpuProfiler.StartCPUProfile(@params.Dir);
        if (err is not null)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: failed to start CPU profile: {err}");
        }
        return null;
    }

    // session.go:792-798.
    private object? HandleStopCPUProfile(global::Ts.Core.RequestContext ctx)
    {
        bool ok = cpuProfiler.TryStopCPUProfile(out string filePath, out string? err);
        if (!ok)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: failed to stop CPU profile: {err}");
        }
        return new ProfileResult { File = filePath };
    }

    // session.go:800-809.
    private object? HandleSaveHeapProfile(global::Ts.Core.RequestContext ctx, ProfileParams @params)
    {
        if (@params is null || @params.Dir == "")
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: dir is required");
        }
        string filePath;
        try
        {
            filePath = global::Ts.Profiling.Pprof.SaveHeapProfile(@params.Dir);
        }
        catch (Exception e)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: failed to save heap profile: {e.Message}");
        }
        return new ProfileResult { File = filePath };
    }

    // HandleNotification implements Handler. (session.go:811-815)
    public void HandleNotification(global::Ts.Core.RequestContext ctx, string method, byte[]? @params)
    {
        // TODO: Implement notification handling
    }

    // session.go:817-822.
    private InitializeResponse HandleInitialize(global::Ts.Core.RequestContext ctx)
    {
        return new InitializeResponse
        {
            UseCaseSensitiveFileNames = projectSession.FS().UseCaseSensitiveFileNames(),
            CurrentDirectory = projectSession.GetCurrentDirectory(),
        };
    }

    // handleUpdateSnapshot creates a new snapshot, optionally opening or closing
    // projects and files. With no args, it adopts the latest LSP state. Opens and
    // closes are ref-counted per session: the session holds at most one ref per
    // project/file, so repeated opens are idempotent and a close only releases a ref
    // the session is actually holding. (session.go:824-972)
    private UpdateSnapshotResponse HandleUpdateSnapshot(global::Ts.Core.RequestContext ctx, UpdateSnapshotParams @params)
    {
        // Fully serialize updates: snapshot creation, ref tracking, and the
        // latestSnapshot/diff bookkeeping must be atomic with respect to other updates,
        // otherwise concurrent updates could compute diffs against a non-adjacent
        // snapshot or leave latestSnapshot pointing at a stale snapshot.
        lock (updateMu)
        {
            global::Ts.Project.FileChangeSummary fileChanges = ToFileChangeSummary(@params.FileChanges);

            // PORT-NOTE(api): APISnapshotRequest's properties are init-only
            // (Project/Snapshot.cs:350-360), so the request is built after the loops;
            // Go accumulates into the struct field-by-field (session.go:889-940).
            global::Ts.Collections.Set<string>? reqOpenProjects = null;
            global::Ts.Collections.Set<global::Ts.TsPath.Path>? reqCloseProjects = null;
            global::Ts.Collections.Set<global::Ts.LsProto.DocumentUri>? reqOpenFiles = null;
            global::Ts.Collections.Set<global::Ts.TsPath.Path>? reqCloseFiles = null;

            // Open projects: only take a new ref for projects we aren't already holding open.
            var openedProjects = new List<global::Ts.TsPath.Path>();
            foreach (DocumentIdentifier p in @params.OpenProjects ?? [])
            {
                string configFileName = p.ToAbsoluteFileName(projectSession.GetCurrentDirectory());
                global::Ts.TsPath.Path configPath = ToPath(configFileName);
                if (openProjects.Has(configPath))
                {
                    continue;
                }
                reqOpenProjects ??= new global::Ts.Collections.Set<string>(@params.OpenProjects?.Length ?? 0);
                reqOpenProjects.Add(configFileName);
                openedProjects.Add(configPath);
            }

            // Close projects: only release a ref we currently hold.
            var closedProjects = new List<global::Ts.TsPath.Path>();
            foreach (DocumentIdentifier p in @params.CloseProjects ?? [])
            {
                global::Ts.TsPath.Path configPath = ToPath(p.ToAbsoluteFileName(projectSession.GetCurrentDirectory()));
                if (!openProjects.Has(configPath))
                {
                    continue;
                }
                reqCloseProjects ??= new global::Ts.Collections.Set<global::Ts.TsPath.Path>(@params.CloseProjects?.Length ?? 0);
                reqCloseProjects.Add(configPath);
                closedProjects.Add(configPath);
            }

            // Open files: only open files we aren't already holding open, so each file is
            // held by at most one API ref from this session.
            var openedFiles = new List<global::Ts.TsPath.Path>();
            foreach (DocumentIdentifier f in @params.OpenFiles ?? [])
            {
                global::Ts.LsProto.DocumentUri uri = f.ToUri(projectSession.GetCurrentDirectory());
                global::Ts.TsPath.Path path = ToPath(uri.FileName());
                if (openFiles.Has(path))
                {
                    continue;
                }
                reqOpenFiles ??= new global::Ts.Collections.Set<global::Ts.LsProto.DocumentUri>(@params.OpenFiles?.Length ?? 0);
                reqOpenFiles.Add(uri);
                openedFiles.Add(path);
            }

            // Close files: only release a ref we currently hold.
            var closedFiles = new List<global::Ts.TsPath.Path>();
            foreach (DocumentIdentifier f in @params.CloseFiles ?? [])
            {
                global::Ts.TsPath.Path path = ToPath(f.ToUri(projectSession.GetCurrentDirectory()).FileName());
                if (!openFiles.Has(path))
                {
                    continue;
                }
                reqCloseFiles ??= new global::Ts.Collections.Set<global::Ts.TsPath.Path>(@params.CloseFiles?.Length ?? 0);
                reqCloseFiles.Add(path);
                closedFiles.Add(path);
            }

            var apiRequest = new global::Ts.Project.Snapshot.APISnapshotRequest
            {
                OpenProjects = reqOpenProjects,
                CloseProjects = reqCloseProjects,
                OpenFiles = reqOpenFiles,
                CloseFiles = reqCloseFiles,
            };

            // Even when nothing is opened or closed, APIUpdate ensures all projects and
            // files opened by the API are up to date. For an API connected to an LSP server,
            // this brings the API state up to date with the LSP state and ensures projects
            // the API cares about are ready to be queried.
            global::Ts.Project.Snapshot snapshot;
            try
            {
                snapshot = projectSession.APIUpdate(ctx, fileChanges, apiRequest);
            }
            catch (Exception e)
            {
                throw new ApiRequestException($"{ProtoErrors.ErrClientError}: failed to update snapshot: {e.Message}");
            }

            // Commit ref tracking now that the update succeeded.
            foreach (global::Ts.TsPath.Path configPath in openedProjects)
            {
                openProjects.Add(configPath);
            }
            foreach (global::Ts.TsPath.Path configPath in closedProjects)
            {
                openProjects.Delete(configPath);
            }
            foreach (global::Ts.TsPath.Path path in openedFiles)
            {
                openFiles.Add(path);
            }
            foreach (global::Ts.TsPath.Path path in closedFiles)
            {
                openFiles.Delete(path);
            }

            // Create or ref-count snapshot data, then atomically read the previous latest
            // snapshot (the diff base) and advance latestSnapshot to the new handle.
            // If the same snapshot ID is returned (no changes), we increment the ref count
            // so each client-side Snapshot can be disposed independently.
            SnapshotId handle = SnapshotHandle(snapshot);
            snapshotsMu.EnterWriteLock();
            try
            {
                if (snapshots.TryGetValue(handle, out SnapshotData? existing))
                {
                    // Same snapshot already stored — release the caller's ref since
                    // the stored snapshot already has one, and bump the API refcount.
                    snapshot.Deref(projectSession);
                    existing.RefCount++;
                }
                else
                {
                    var sd = new SnapshotData
                    {
                        Snapshot = snapshot,
                        RefCount = 1,
                    };
                    snapshots[handle] = sd;
                }
            }
            finally
            {
                snapshotsMu.ExitWriteLock();
            }
            snapshotsMu.EnterReadLock();
            SnapshotData? prevSd = snapshots.TryGetValue(latestSnapshot, out SnapshotData? found) ? found : null;
            snapshotsMu.ExitReadLock();
            snapshotsMu.EnterWriteLock();
            latestSnapshot = handle;
            snapshotsMu.ExitWriteLock();

            // Build projects list
            List<global::Ts.Project.Project> projects = snapshot.ProjectCollection.Projects();
            var projectResponses = new List<ProjectResponse>(projects.Count);
            foreach (global::Ts.Project.Project proj in projects)
            {
                if (proj.CommandLine is null)
                {
                    continue;
                }
                projectResponses.Add(ProtoFunctions.NewProjectResponse(proj));
            }

            // Compute changes from the previous latest snapshot
            SnapshotChanges? changes = null;
            if (prevSd is not null)
            {
                changes = ComputeSnapshotChanges(prevSd.Snapshot, snapshot);
            }

            return new UpdateSnapshotResponse
            {
                Snapshot = handle,
                Projects = [.. projectResponses],
                Changes = changes,
            };
        }
    }

    // handleRelease decrements the ref count for a snapshot.
    // The snapshot and its registries are only cleaned up when the ref count reaches zero.
    // (session.go:974-995)
    private object? HandleRelease(global::Ts.Core.RequestContext ctx, ReleaseParams? @params)
    {
        if (@params is null || @params.Snapshot.Value == 0)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: empty handle");
        }

        snapshotsMu.EnterWriteLock();
        try
        {
            if (!snapshots.TryGetValue(@params.Snapshot, out SnapshotData? sd) || sd is null)
            {
                throw new ApiRequestException($"{ProtoErrors.ErrClientError}: snapshot {@params.Snapshot.Value} not found");
            }
            sd.RefCount--;
            if (sd.RefCount <= 0)
            {
                snapshots.Remove(@params.Snapshot);
                // Release the API session's ref on the project snapshot.
                sd.Snapshot.Deref(projectSession);
            }
        }
        finally
        {
            snapshotsMu.ExitWriteLock();
        }
        return true;
    }

    // handleGetDefaultProjectForFile returns the default project for a given file,
    // or nil if no project currently contains the file. (session.go:997-1012)
    private ProjectResponse? HandleGetDefaultProjectForFile(global::Ts.Core.RequestContext ctx, GetDefaultProjectForFileParams @params)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.LsProto.DocumentUri uri = @params.File.ToUri(projectSession.GetCurrentDirectory());
        global::Ts.Project.Project? proj = sd.Snapshot.GetDefaultProject(uri);
        if (proj is null)
        {
            return null;
        }

        return ProtoFunctions.NewProjectResponse(proj);
    }

    // handleParseConfigFile parses a tsconfig.json file and returns its contents.
    // (session.go:1014-1044)
    private ConfigFileResponse HandleParseConfigFile(global::Ts.Core.RequestContext ctx, ParseConfigFileParams @params)
    {
        string configFileName = @params.File.ToAbsoluteFileName(projectSession.GetCurrentDirectory());
        if (!projectSession.FS().TryReadFile(configFileName, out string configFileContent))
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: could not read file \"{configFileName}\"");
        }

        string configDir = global::Ts.TsPath.TsPath.GetDirectoryPath(configFileName);
        global::Ts.TsOptions.TsConfigSourceFile tsConfigSourceFile = global::Ts.TsOptions.TsConfigParsing.NewTsconfigSourceFileFromFilePath(
            configFileName,
            ToPath(configFileName),
            configFileContent);
        global::Ts.TsOptions.ParsedCommandLine parsedCommandLine = global::Ts.TsOptions.TsConfigParsing.ParseJsonSourceFileConfigFileContent(
            tsConfigSourceFile,
            projectSession,
            configDir,
            null, /*existingOptions*/
            null, /*existingOptionsRaw*/
            configFileName,
            null, /*resolutionStack*/
            null, /*extraFileExtensions*/
            null /*extendedConfigCache*/);

        return new ConfigFileResponse
        {
            FileNames = parsedCommandLine.FileNames() ?? [],
            Options = parsedCommandLine.CompilerOptions(),
        };
    }

    // handleGetSourceFile returns a source file from a project within a snapshot.
    // (session.go:1046-1079)
    private object? HandleGetSourceFile(global::Ts.Core.RequestContext ctx, GetSourceFileParams @params)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Compiler.Program program = sd.GetProgram(@params.Project);

        global::Ts.Ast.SourceFile? sourceFile = program.GetSourceFile(@params.File.ToFileName());
        if (sourceFile is null)
        {
            if (useBinaryResponses)
            {
                return new RawBinary([]);
            }
            return null;
        }

        // Encode the full source file.
        byte[] data = Ts.Api.Encoder.Encoder.EncodeSourceFile(sourceFile).Data;

        // Return raw binary for msgpack protocol, or base64 for JSON
        if (useBinaryResponses)
        {
            return new RawBinary(data);
        }
        return new SourceFileResponse
        {
            Data = Convert.ToBase64String(data),
        };
    }

    // handleGetSourceFileNames returns file names of all source files in a project.
    // (session.go:1081-1099)
    private string[] HandleGetSourceFileNames(global::Ts.Core.RequestContext ctx, GetSourceFileNamesParams @params)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Compiler.Program program = sd.GetProgram(@params.Project);

        global::Ts.Ast.SourceFile[] sourceFiles = program.SourceFiles();
        var result = new string[sourceFiles.Length];
        for (int i = 0; i < sourceFiles.Length; i++)
        {
            result[i] = sourceFiles[i].FileName();
        }
        return result;
    }

    // handleGetSourceFileMetadata returns program-stored metadata for a single source file.
    // The client fetches this lazily per file and caches it. (session.go:1101-1127)
    private SourceFileMetadata? HandleGetSourceFileMetadata(global::Ts.Core.RequestContext ctx, GetSourceFileParams @params)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Compiler.Program program = sd.GetProgram(@params.Project);

        global::Ts.Ast.SourceFile? sourceFile = program.GetSourceFile(@params.File.ToFileName());
        if (sourceFile is null)
        {
            return null;
        }

        global::Ts.Ast.SourceFileMetaData metaData = program.GetSourceFileMetaData(sourceFile.Path());
        return new SourceFileMetadata
        {
            IsDefaultLibrary = program.IsSourceFileDefaultLibrary(sourceFile.Path()),
            IsFromExternalLibrary = program.IsSourceFileFromExternalLibrary(sourceFile),
            PackageJsonType = metaData.PackageJsonType,
            PackageJsonDirectory = metaData.PackageJsonDirectory,
            ImpliedNodeFormat = metaData.ImpliedNodeFormat,
        };
    }

    // session.go:2807-2809 — formatSessionID.
    private static string FormatSessionId(ulong id)
    {
        return $"api-session-{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    // session.go:2811-2814 — toPath converts a file name to a normalized path.
    private global::Ts.TsPath.Path ToPath(string fileName)
    {
        return global::Ts.TsPath.TsPath.ToPath(fileName, projectSession.GetCurrentDirectory(), projectSession.FS().UseCaseSensitiveFileNames());
    }

    // session.go:2816-2844 — toFileChangeSummary converts API file changes to a
    // project.FileChangeSummary.
    private global::Ts.Project.FileChangeSummary ToFileChangeSummary(ApiFileChanges? changes)
    {
        if (changes is null)
        {
            return new global::Ts.Project.FileChangeSummary();
        }
        var summary = new global::Ts.Project.FileChangeSummary();
        if (changes.InvalidateAll)
        {
            summary.InvalidateAll = true;
            summary.IncludesWatchChangeOutsideNodeModules = true;
            return summary;
        }
        string cwd = projectSession.GetCurrentDirectory();
        foreach (DocumentIdentifier doc in changes.Changed ?? [])
        {
            summary.Changed.Add(doc.ToUri(cwd));
        }
        foreach (DocumentIdentifier doc in changes.Created ?? [])
        {
            summary.Created.Add(doc.ToUri(cwd));
        }
        foreach (DocumentIdentifier doc in changes.Deleted ?? [])
        {
            summary.Deleted.Add(doc.ToUri(cwd));
        }
        if (summary.Changed.Len() + summary.Created.Len() + summary.Deleted.Len() > 0)
        {
            summary.IncludesWatchChangeOutsideNodeModules = true;
        }
        return summary;
    }
}
