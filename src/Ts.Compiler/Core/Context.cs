// Ported from tsgo internal/core/context.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System.Threading;

namespace Ts.Core;

// Go: internal/core/context.go — context.Context carried three values across the
// checker-pool boundary: cancellation, requestID, and checker lifetime. Per plan/01 §4.4
// (and 05 §11) these become explicit fields on an immutable RequestContext rather than
// context.Value key/value pairs; AsyncLocal is deliberately rejected (opaque under AOT,
// hard to audit). The Go key type / WithValue plumbing (context.go:7-12) dissolves.

// Go: type CheckerLifetime int (context.go:25-31). The Go member prefix is dropped;
// Temporary MUST stay 0 so default(RequestContext) yields Go's zero (Temporary), matching
// GetCheckerLifetime's fallback (context.go:37-42).
public enum CheckerLifetime
{
    Temporary,
    Diagnostics,
    Api,
}

public readonly struct RequestContext
{
    private readonly string? requestId;

    public CancellationToken Cancellation { get; init; }

    // Go GetRequestID returns "" when unset (context.go:18-23); the backing field is null
    // for default(RequestContext), so the getter coalesces to "".
    public string RequestId
    {
        get => requestId ?? "";
        init => requestId = value;
    }

    public CheckerLifetime CheckerLifetime { get; init; }

    // Go carries the locale through locale.WithLocale(ctx, ...) context values
    // (locale/locale.go); per plan/01 §4.4 it becomes an explicit field. The struct
    // default is locale.Locale.Default (Go's zero language.Tag == Und), matching
    // locale.FromContext's unset fallback.
    public global::Ts.Locale.Locale Locale { get; init; }

    // Go: locale.WithLocale (locale/locale.go).
    public RequestContext WithLocale(global::Ts.Locale.Locale locale) => this with { Locale = locale };

    // Corresponds to Go's ctx.Done()==nil test (plan/01 §4.4).
    public bool CanBeCanceled => Cancellation.CanBeCanceled;

    // Go: WithCheckerLifetime (context.go:33-35).
    public RequestContext WithCheckerLifetime(CheckerLifetime lifetime) => this with { CheckerLifetime = lifetime };

    // Go: WithRequestID (context.go:14-16).
    public RequestContext WithRequestId(string id) => this with { RequestId = id };

    // The unset/background context (Go context.Background): no cancellation, empty request
    // id, Temporary lifetime.
    public static RequestContext Background => default;
}
