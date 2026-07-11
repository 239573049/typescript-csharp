// Ported from tsgo internal/fswatch/canonicalize_darwin.go +
// canonicalize_other.go.
//
// PORT-NOTE: 非 1:1，FileSystemWatcher 重写. Go selects the darwin vs. no-op
// implementation via build tags (//go:build darwin && (amd64 || arm64)). In C#
// there is a single method with a runtime OS check, preserving the same
// semantics: NFC-normalize on macOS, identity elsewhere. plan/06 §8.5 pins
// String.Normalize as the alignment for canonicalize_darwin.go.

using System;
using System.Text;

namespace Ts.FsWatch;

internal static class Canonicalize
{
    // canonicalizePath returns the path in the form the library uses for
    // internal bookkeeping and event delivery. On macOS, paths from the OS
    // watcher arrive using whatever Unicode normalization form is stored on
    // disk; usually NFC, but sometimes NFD. APFS resolves either form to the
    // same inode, but raw string comparisons against caller-supplied paths
    // (typically NFC) silently break. Normalizing every path the library
    // ingests to NFC keeps watch keys, dirWatch lookups, WatchFile filters,
    // and event paths all in one consistent form.
    //
    // On every other platform this is a no-op (canonicalize_other.go): those
    // watchers report paths using the same bytes the caller provided.
    public static string CanonicalizePath(string p)
    {
        if (OperatingSystem.IsMacOS())
        {
            return NormalizeNFC(p);
        }
        return p;
    }

    // normalizeNFC mirrors the Go darwin helper (normalizeNFC): return p in
    // Unicode Normalization Form C. String.Normalize is ordinal/culture-free.
    private static string NormalizeNFC(string p)
    {
        if (p.Length == 0)
        {
            return p;
        }
        if (p.IsNormalized(NormalizationForm.FormC))
        {
            return p;
        }
        return p.Normalize(NormalizationForm.FormC);
    }
}
