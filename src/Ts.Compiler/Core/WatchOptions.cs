// Ported from tsgo internal/core/watchoptions.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;

namespace Ts.Core;

// PORT-NOTE: Go `*int` → `int?`; `[]string` → `string[]?` (nil preserved). This struct
// carries no `noCopy`, so it maps to a plain reference class. Field order matches Go.
public sealed class WatchOptions
{
    public int? Interval;
    public WatchFileKind FileKind;
    public WatchDirectoryKind DirectoryKind;
    public PollingKind FallbackPolling;
    public Tristate SyncWatchDir;
    public string[]? ExcludeDir;
    public string[]? ExcludeFiles;
}

public enum WatchFileKind
{
    None = 0,
    FixedPollingInterval = 1,
    PriorityPollingInterval = 2,
    DynamicPriorityPolling = 3,
    FixedChunkSizePolling = 4,
    UseFsEvents = 5,
    UseFsEventsOnParentDirectory = 6,
}

public enum WatchDirectoryKind
{
    None = 0,
    UseFsEvents = 1,
    FixedPollingInterval = 2,
    DynamicPriorityPolling = 3,
    FixedChunkSizePolling = 4,
}

public enum PollingKind
{
    None = 0,
    FixedInterval = 1,
    PriorityInterval = 2,
    DynamicPriority = 3,
    FixedChunkSize = 4,
}

// Go `func (w *WatchOptions) WatchInterval()` tolerates a nil receiver, so it becomes a
// nil-tolerant extension method (05-conventions.md §10.3). Go's time.Duration maps to
// TimeSpan; the millisecond values are represented exactly.
public static class WatchOptionsExtensions
{
    public static TimeSpan WatchInterval(this WatchOptions? w)
    {
        var watchInterval = TimeSpan.FromMilliseconds(2000);
        if (w != null && w.Interval != null)
        {
            watchInterval = TimeSpan.FromMilliseconds(w.Interval.Value);
        }
        return watchInterval;
    }
}
