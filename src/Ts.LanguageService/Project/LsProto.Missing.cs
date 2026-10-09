// Faithful ports of the subset of tsgo internal/lsp/lsproto types that the project
// package consumes but that the C# Ts.LsProto foundation (Lsp/Protocol.cs) does not
// carry yet. PORT-NOTE (location deviation, registered): Lsp/ is off-limits in this
// pass, so these live beside their only consumer; when the generated protocol surface
// lands in Ts.LsProto, delete this file and redirect call sites (05-conventions §16
// "hosted here" precedent, cf. Ts.Ls.LsDeps).

using System;
using System.Collections.Generic;
using static global::Ts.LsProto.DocumentUriExtensions;

namespace Ts.Project;

// lsp_generated.go:10149-10157 — WatchKind (bit flags).
[Flags]
public enum WatchKind : uint
{
    // lsp_generated.go:10153 — WatchKindCreate Want to watch create events.
    Create = 1,
    // lsp_generated.go:10155 — WatchKindChange Want to watch change events.
    Change = 2,
    // lsp_generated.go:10157 — WatchKindDelete Want to watch delete events.
    Delete = 4,
}

// lsp_generated.go:10126-10134 — FileChangeType.
public enum FileChangeType : uint
{
    // lsp_generated.go:10130 — Created.
    Created = 1,
    // lsp_generated.go:10132 — Changed.
    Changed = 2,
    // lsp_generated.go:10134 — Deleted.
    Deleted = 3,
}

// lsp_generated.go:10107-10116 — PositionEncodingKind (string enum): now provided by
// the generated Ts.LsProto.PositionEncodingKind record struct (Lsp/Protocol.g.cs); the
// stand-in const class that lived here was deleted per this file's header once the
// generated surface landed (SessionOptions.PositionEncoding binds to it).

// lsp_generated.go — GlobPattern. Exactly one of Pattern / RelativePattern is set.
public sealed class GlobPattern
{
    public string? Pattern { get; set; }

    public RelativePattern? RelativePattern { get; set; }
}

// lsp_generated.go — RelativePattern. BaseUri is a workspace folder or a URI.
public sealed class RelativePattern
{
    public WorkspaceFolderOrURI BaseUri { get; set; } = null!;

    public string Pattern { get; set; } = "";
}

// lsp_generated.go — WorkspaceFolderOrURI. Exactly one arm is set.
public sealed class WorkspaceFolderOrURI
{
    public global::Ts.LsProto.WorkspaceFolder? WorkspaceFolder { get; set; }

    public global::Ts.LsProto.DocumentUri? URI { get; set; }
}

// lsp_generated.go — FileSystemWatcher.
public sealed class FileSystemWatcher
{
    public GlobPattern GlobPattern { get; set; } = new();

    // The kind of events of interest, defaulted (lsp.go: defaults to 7 = create|change|delete).
    public WatchKind? Kind { get; set; }
}

// lsp_generated.go:5446-5452 — FileEvent.
public sealed class FileEvent
{
    // The file's uri.
    public global::Ts.LsProto.DocumentUri Uri { get; set; }

    // The change type.
    public FileChangeType Type { get; set; }
}

// lsp_generated.go:17221-17244 — ResolvedClientCapabilities, restricted to the members
// the project package reads (Workspace.DidChangeWatchedFiles.RelativePatternSupport,
// TextDocument.PublishDiagnostics.{RelatedInformation,TagSupport.ValueSet},
// VSSupportsVisualStudioExtensions). Go builds this via (*ClientCapabilities).Resolve();
// the C# Ts.LsProto.ClientCapabilities carries no fields yet, so Resolve yields the
// protocol defaults. When the generated capabilities land, replace the body.
public sealed class ResolvedClientCapabilities
{
    public ResolvedWorkspaceClientCapabilities Workspace { get; set; } = new();

    public ResolvedTextDocumentClientCapabilities TextDocument { get; set; } = new();

    public bool VSSupportsVisualStudioExtensions { get; set; }

    // lsp_generated.go — (*ClientCapabilities).Resolve (subset).
    public static ResolvedClientCapabilities Resolve(global::Ts.LsProto.ClientCapabilities? caps)
    {
        // PORT-NOTE: the C# ClientCapabilities foundation is empty; every field
        // resolves to its protocol default (absent ⇒ false/nil), which matches Go's
        // zero-value behavior for a capabilities-less client.
        _ = caps;
        return new ResolvedClientCapabilities();
    }
}

// lsp_generated.go — ResolvedWorkspaceClientCapabilities (subset).
public sealed class ResolvedWorkspaceClientCapabilities
{
    public ResolvedDidChangeWatchedFilesClientCapabilities DidChangeWatchedFiles { get; set; } = new();
}

// lsp_generated.go — ResolvedDidChangeWatchedFilesClientCapabilities (subset).
public sealed class ResolvedDidChangeWatchedFilesClientCapabilities
{
    // DidChangeWatchedFilesRelativePatternSupportClientCapabilities — relative pattern support.
    public bool RelativePatternSupport { get; set; }
}

// lsp_generated.go — ResolvedTextDocumentClientCapabilities (subset).
public sealed class ResolvedTextDocumentClientCapabilities
{
    public ResolvedPublishDiagnosticsClientCapabilities PublishDiagnostics { get; set; } = new();
}

// lsp_generated.go — ResolvedPublishDiagnosticsClientCapabilities (subset).
public sealed class ResolvedPublishDiagnosticsClientCapabilities
{
    // The client supports additional metadata in the diagnostic's relatedInformation.
    public bool RelatedInformation { get; set; }

    // Client supports the tag property to provide meta data about a diagnostic.
    public DiagnosticTagSupport? TagSupport { get; set; }
}

// lsp_generated.go — TagSupport capability: { valueSet: DiagnosticTag[] }.
public sealed class DiagnosticTagSupport
{
    public global::Ts.LsProto.DiagnosticTag[] ValueSet { get; set; } = [];
}

// lsp.go:293 — GetClientCapabilities reads the resolved capabilities from the request
// context. plan/01 §4.4 fixed the C# RequestContext shape (no context values), so the
// session-scoped capabilities travel on SessionOptions instead (identical semantics:
// immutable for the server lifetime). This helper is the single access point so the
// Go call shape stays recognizable.
public static class ClientCapabilitiesAccess
{
    public static ResolvedClientCapabilities GetClientCapabilities(SessionOptions options) =>
        options.ClientCapabilities;
}

// lsp_generated.go — ProjectInfoTelemetryMeasurements (subset used by session.go).
public sealed class ProjectInfoTelemetryMeasurements
{
    public double TsFileCount { get; set; }
    public double TsFileSize { get; set; }
    public double JsFileCount { get; set; }
    public double JsFileSize { get; set; }
    public double TsxFileCount { get; set; }
    public double TsxFileSize { get; set; }
    public double JsxFileCount { get; set; }
    public double JsxFileSize { get; set; }
    public double DtsFileCount { get; set; }
    public double DtsFileSize { get; set; }
}

// lsp_generated.go — ProjectInfoTelemetryEvent.
public sealed class ProjectInfoTelemetryEvent
{
    public Dictionary<string, string> Properties { get; set; } = [];

    public ProjectInfoTelemetryMeasurements Measurements { get; set; } = new();
}

// lsp_generated.go — PerformanceStatsTelemetryMeasurements (subset used by session.go;
// Go-runtime-specific counters resolve to their C# equivalents or 0 — see Session.cs).
public sealed class PerformanceStatsTelemetryMeasurements
{
    public double MemoryUsedBytes { get; set; }
    public double GoMemLimit { get; set; }
    public double GoGCPercent { get; set; }
    public double HeapGoalBytes { get; set; }
    public double HeapLiveBytes { get; set; }
    public double HeapObjectCount { get; set; }
    public double HeapStackBytes { get; set; }
    public double HeapReleasedBytes { get; set; }
    public double HeapFreeBytes { get; set; }
    public double GcScanHeapBytes { get; set; }
    public double GoMaxProcs { get; set; }
    public double GoroutineCount { get; set; }
    public double GcCyclesTotal { get; set; }
    public double GcCPUSeconds { get; set; }
    public double UserCPUSeconds { get; set; }
    public double SystemMemTotal { get; set; }
    public double SystemMemUsed { get; set; }
    public double OpenFileCount { get; set; }
    public double UptimeSeconds { get; set; }
    public double ProjectCount { get; set; }
    public double ConfigCount { get; set; }
    public double CachedDiskFileCount { get; set; }
    public double AutoImportProjectBucketCount { get; set; }
    public double AutoImportNodeModulesBucketCount { get; set; }
    public double AutoImportUniquePackageCount { get; set; }
    public double AutoImportProjectExportCount { get; set; }
    public double AutoImportProjectFileCount { get; set; }
    public double AutoImportNodeModulesExportCount { get; set; }
    public double AutoImportNodeModulesFileCount { get; set; }
    public double AutoImportNodeModulesUnfilteredBucketCount { get; set; }
}

// lsp_generated.go — PerformanceStatsTelemetryEvent.
public sealed class PerformanceStatsTelemetryEvent
{
    public PerformanceStatsTelemetryMeasurements Measurements { get; set; } = new();
}

// lsp.go:52-55 — DocumentUri.Path.
public static class DocumentUriPathExtensions
{
    public static global::Ts.TsPath.Path Path(this global::Ts.LsProto.DocumentUri uri, bool useCaseSensitiveFileNames)
    {
        string fileName = uri.FileName();
        return global::Ts.TsPath.TsPath.ToPath(fileName, "", useCaseSensitiveFileNames);
    }
}

// lsp_generated.go — TelemetryEvent. Exactly one arm is set.
public sealed class TelemetryEvent
{
    public PerformanceStatsTelemetryEvent? PerformanceStatsTelemetryEvent { get; set; }

    public ProjectInfoTelemetryEvent? ProjectInfoTelemetryEvent { get; set; }
}
