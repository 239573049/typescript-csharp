// Ported from tsgo internal/core/nodemodules.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;

namespace Ts.Core;

public static partial class Core
{
    // require('module').builtinModules.filter(x => !x.match(/^(?:_|node:)/))
    // Go: map[string]bool constant set → FrozenSet<string> with Ordinal comparer (plan/10 §2.3.1).
    public static readonly FrozenSet<string> UnprefixedNodeCoreModules = new[]
    {
        "assert",
        "assert/strict",
        "async_hooks",
        "buffer",
        "child_process",
        "cluster",
        "console",
        "constants",
        "crypto",
        "dgram",
        "diagnostics_channel",
        "dns",
        "dns/promises",
        "domain",
        "events",
        "fs",
        "fs/promises",
        "http",
        "http2",
        "https",
        "inspector",
        "inspector/promises",
        "module",
        "net",
        "os",
        "path",
        "path/posix",
        "path/win32",
        "perf_hooks",
        "process",
        "punycode",
        "querystring",
        "readline",
        "readline/promises",
        "repl",
        "stream",
        "stream/consumers",
        "stream/promises",
        "stream/web",
        "string_decoder",
        "sys",
        "timers",
        "timers/promises",
        "tls",
        "trace_events",
        "tty",
        "url",
        "util",
        "util/types",
        "v8",
        "vm",
        "wasi",
        "worker_threads",
        "zlib",
    }.ToFrozenSet(StringComparer.Ordinal);

    // require('module').builtinModules.filter(x => x.startsWith('node:'))
    public static readonly FrozenSet<string> ExclusivelyPrefixedNodeCoreModules = new[]
    {
        "node:quic",
        "node:sea",
        "node:sqlite",
        "node:test",
        "node:test/reporters",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Go: sync.OnceValue → Lazy<T> (05 §11); call sites NodeCoreModules() → NodeCoreModules.Value.
    public static readonly Lazy<FrozenSet<string>> NodeCoreModules = new(() =>
    {
        var nodeCoreModules = new HashSet<string>(
            UnprefixedNodeCoreModules.Count * 2 + ExclusivelyPrefixedNodeCoreModules.Count,
            StringComparer.Ordinal);
        // map-iter: 序无关（构建集合，结果与迭代序无关）
        foreach (string unprefixed in UnprefixedNodeCoreModules)
        {
            nodeCoreModules.Add(unprefixed);
            nodeCoreModules.Add("node:" + unprefixed);
        }
        nodeCoreModules.UnionWith(ExclusivelyPrefixedNodeCoreModules); // Go: maps.Copy
        return nodeCoreModules.ToFrozenSet(StringComparer.Ordinal);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string NonRelativeModuleNameForTypingCache(string moduleName)
    {
        if (NodeCoreModules.Value.Contains(moduleName))
        {
            return "node";
        }
        return moduleName;
    }
}
