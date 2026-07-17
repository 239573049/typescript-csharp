// Ported from tsgo internal/execute/watchmanager/watchbackend.go.

using System;
using System.Collections.Generic;
using Ts.FsWatch;

namespace Ts.Execute.WatchManagement;

// watchbackend.go:11-15 — WatchBackend abstracts fswatch.Watcher for testing
public interface WatchBackend
{
    IWatch WatchDirectory(string dir, WatchCallback fn, bool recursive, Func<string, bool>? ignore);
    IReadOnlyList<IWatch> WatchDirectories(IReadOnlyList<WatchDirectoryRequestWm> requests);
}

// watchbackend.go:17-22
public struct WatchDirectoryRequestWm
{
    public string Dir;
    public WatchCallback Callback;
    public bool Recursive;
    public Func<string, bool>? Ignore;
}

// watchbackend.go:24-28 — CommandLineTestingWithWatchBackend is an optional extension of
// [CommandLineTesting] that supplies a [WatchBackend] for test mode
public interface ICommandLineTestingWithWatchBackend
{
    WatchBackend WatchBackend();
}

// watchbackend.go:30-70
public sealed class FSWatchBackend : WatchBackend
{
    public IWatcher Inner = null!;

    public IWatch WatchDirectory(string dir, WatchCallback fn, bool recursive, Func<string, bool>? ignore)
    {
        var closers = WatchDirectories([new WatchDirectoryRequestWm
        {
            Dir = dir,
            Callback = fn,
            Recursive = recursive,
            Ignore = ignore,
        }]);
        return closers[0];
    }

    public IReadOnlyList<IWatch> WatchDirectories(IReadOnlyList<WatchDirectoryRequestWm> requests)
    {
        var fswatchRequests = new global::Ts.FsWatch.WatchDirectoryRequest[requests.Count];
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var opts = new List<IWatchOption>();
            if (request.Recursive)
            {
                opts.Add(global::Ts.FsWatch.Watcher.WithRecursive());
            }
            if (request.Ignore != null)
            {
                opts.Add(global::Ts.FsWatch.Watcher.WithIgnore(request.Ignore));
            }
            fswatchRequests[i] = new global::Ts.FsWatch.WatchDirectoryRequest
            {
                Dir = request.Dir,
                Callback = request.Callback,
                Options = opts.ToArray(),
            };
        }
        return Inner.WatchDirectories(fswatchRequests);
    }
}

public static class WatchPathUtil
{
    // watchbackend.go:72-78
    public static bool ShouldIgnoreWatchPath(string path)
    {
        var p = global::Ts.TsPath.TsPath.NormalizeSlashes(path);
        return p.EndsWith("/.git", StringComparison.Ordinal) ||
            p.Contains("/.git/", StringComparison.Ordinal) ||
            p.Contains("/node_modules/.", StringComparison.Ordinal) ||
            p.Contains("/.#", StringComparison.Ordinal);
    }

    // watchbackend.go:80-88
    public static bool CanWatchDirectory(string dir)
    {
        var components = global::Ts.TsPath.TsPath.GetPathComponents(dir, "");
        var length = components.Count;
        if (length <= 2)
        {
            return false;
        }
        var rootLength = PerceivedOsRootLengthForWatching(components);
        return length > rootLength + 1;
    }

    // watchbackend.go:90-118
    public static int PerceivedOsRootLengthForWatching(List<string> components)
    {
        var length = components.Count;
        if (length <= 1)
        {
            return 1;
        }
        var root = components[0];
        var indexAfterOsRoot = 1;
        var isDosStyle = root.Length >= 2 && global::Ts.TsPath.TsPath.IsVolumeCharacter(root[0]) && root[1] == ':';

        if (root != "/" && !isDosStyle && components.Count > 1)
        {
            if (components[1].Length >= 2 && global::Ts.TsPath.TsPath.IsVolumeCharacter(components[1][0]) && components[1].EndsWith("$", StringComparison.Ordinal))
            {
                if (length == 2)
                {
                    return 2;
                }
                indexAfterOsRoot = 2;
                isDosStyle = true;
            }
        }

        if (isDosStyle && (indexAfterOsRoot >= length || !string.Equals(components[indexAfterOsRoot], "users", StringComparison.OrdinalIgnoreCase)))
        {
            return indexAfterOsRoot;
        }

        if (indexAfterOsRoot < length && string.Equals(components[indexAfterOsRoot], "workspaces", StringComparison.OrdinalIgnoreCase))
        {
            return indexAfterOsRoot + 1;
        }

        return indexAfterOsRoot + 2;
    }
}
