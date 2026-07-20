// Ported from tsgo internal/testutil/harnessutil/harnessutil.go:505-588 — TracerForBaselining.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ts.TestUtil;

using PathT = global::Ts.TsPath.Path;
using TsPathApi = global::Ts.TsPath.TsPath;

public static class HarnessUtil
{
    // harnessutil.go:41
    public const string FakeTSVersion = "FakeTSVersion";
}

// harnessutil.go:505-517
public sealed class TracerForBaselining
{
    private readonly global::Ts.TsPath.ComparePathsOptions opts;
    private Dictionary<PathT, bool> packageJsonCache;
    private readonly StringBuilder builder;

    public TracerForBaselining(global::Ts.TsPath.ComparePathsOptions opts, StringBuilder builder)
    {
        this.opts = opts;
        packageJsonCache = new Dictionary<PathT, bool>();
        this.builder = builder;
    }

    // harnessutil.go:519-521
    public void Trace(global::Ts.Diagnostics.Message msg, object?[] args)
    {
        builder.Append(SanitizeTrace(global::Ts.Diagnostics.Diagnostics.Localize(msg, global::Ts.Locale.Locale.Default, args), true));
        builder.Append('\n');
    }

    // harnessutil.go:523-525
    public void TraceWithWriter(TextWriter w, string msg, bool usePackageJsonCache)
    {
        w.Write(SanitizeTrace(msg, usePackageJsonCache));
        w.Write('\n');
    }

    // harnessutil.go:527-580
    private string SanitizeTrace(string msg, bool usePackageJsonCache)
    {
        // Version
        var version = "'" + global::Ts.Core.TsVersion.Version() + "'";
        var idx = msg.IndexOf(version, StringComparison.Ordinal);
        if (idx >= 0)
        {
            return msg[..idx] + "'" + HarnessUtil.FakeTSVersion + "'" + msg[(idx + version.Length)..];
        }
        // caching of fs in trace to be replaces with non caching version
        if (msg.EndsWith("' does not exist according to earlier cached lookups.", StringComparison.Ordinal))
        {
            var str = msg[..^"' does not exist according to earlier cached lookups.".Length];
            var file = TrimFilePrefix(str);
            if (usePackageJsonCache)
            {
                var filePath = TsPathApi.ToPath(file, opts.CurrentDirectory, opts.UseCaseSensitiveFileNames);
                if (packageJsonCache.ContainsKey(filePath))
                {
                    return msg;
                }
                packageJsonCache[filePath] = false;
            }
            return $"File '{file}' does not exist.";
        }
        if (msg.EndsWith("' exists according to earlier cached lookups.", StringComparison.Ordinal))
        {
            var str = msg[..^"' exists according to earlier cached lookups.".Length];
            var file = TrimFilePrefix(str);
            if (usePackageJsonCache)
            {
                var filePath = TsPathApi.ToPath(file, opts.CurrentDirectory, opts.UseCaseSensitiveFileNames);
                if (packageJsonCache.ContainsKey(filePath))
                {
                    return msg;
                }
                packageJsonCache[filePath] = true;
            }
            return $"Found 'package.json' at '{file}'.";
        }
        if (usePackageJsonCache)
        {
            if (msg.EndsWith("' does not exist.", StringComparison.Ordinal))
            {
                var str = msg[..^"' does not exist.".Length];
                var file = TrimFilePrefix(str);
                var filePath = TsPathApi.ToPath(file, opts.CurrentDirectory, opts.UseCaseSensitiveFileNames);
                if (!packageJsonCache.ContainsKey(filePath))
                {
                    packageJsonCache[filePath] = false;
                    return msg;
                }
                return $"File '{file}' does not exist according to earlier cached lookups.";
            }
            if (msg.StartsWith("Found 'package.json' at '", StringComparison.Ordinal))
            {
                var str = msg["Found 'package.json' at '".Length..];
                var file = str.EndsWith("'.", StringComparison.Ordinal) ? str[..^2] : str;
                var filePath = TsPathApi.ToPath(file, opts.CurrentDirectory, opts.UseCaseSensitiveFileNames);
                if (!packageJsonCache.ContainsKey(filePath))
                {
                    packageJsonCache[filePath] = true;
                    return msg;
                }
                return $"File '{file}' exists according to earlier cached lookups.";
            }
        }
        return msg;
    }

    private static string TrimFilePrefix(string str) =>
        str.StartsWith("File '", StringComparison.Ordinal) ? str["File '".Length..] : str;

    // harnessutil.go:582-584
    public override string ToString() => builder.ToString();

    // harnessutil.go:586-588
    public void Reset() => packageJsonCache = new Dictionary<PathT, bool>();
}
