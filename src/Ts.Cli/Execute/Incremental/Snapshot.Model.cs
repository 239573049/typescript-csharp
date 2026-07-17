// Ported from tsgo internal/execute/incremental/snapshot.go (data-model half: FileInfo,
// ComputeHash, FileEmitKind, emitSignature). The snapshot struct itself and its
// program-facing methods land with Snapshot.cs in the same construction wave.
//
// ComputeHash is LOAD-BEARING (plan/01 §xxh3 保真分级): the lowercase-hex XXH3-128 output is
// persisted into .tsbuildinfo version/signature fields and appears verbatim in tsbuild
// baselines. Go hashes the UTF-8 bytes of the string and hex-encodes the 16-byte big-endian
// canonical representation; C# must hash UTF-8 bytes (not UTF-16) to match.
// Pinned test vector: xxh3_128("export const a = 10;") = 3a1e9965f8602302ee6ae53756eac8be.

using System;
using System.Text;
using Ts.Core;

namespace Ts.Execute.Incremental;

// snapshot.go:20-30
public sealed class FileInfo
{
    internal string version;
    internal string signature;
    internal bool affectsGlobalScope;
    internal ModuleKind /*ResolutionMode*/ impliedNodeFormat;

    // Go struct-literal construction sites (buildInfo.go GetFileInfo, programtosnapshot.go).
    internal FileInfo(string version = "", string signature = "", bool affectsGlobalScope = false, ModuleKind impliedNodeFormat = ModuleKind.None)
    {
        this.version = version;
        this.signature = signature;
        this.affectsGlobalScope = affectsGlobalScope;
        this.impliedNodeFormat = impliedNodeFormat;
    }

    public string Version() => version;
    public string Signature() => signature;
    public bool AffectsGlobalScope() => affectsGlobalScope;
    public ModuleKind /*ResolutionMode*/ ImpliedNodeFormat() => impliedNodeFormat;
}

public static class IncrementalHash
{
    // snapshot.go:32-39 — ComputeHash. xxh3.HashString128(text).Bytes() yields the canonical
    // big-endian 16-byte form; System.IO.Hashing.XxHash128.Hash returns the same canonical bytes.
    public static string ComputeHash(string text, bool hashWithText)
    {
        var hashBytes = System.IO.Hashing.XxHash128.Hash(Encoding.UTF8.GetBytes(text));
        var hash = Convert.ToHexStringLower(hashBytes);
        if (hashWithText)
        {
            hash += "-" + text;
        }
        return hash;
    }
}

// snapshot.go:41-57
[Flags]
public enum FileEmitKind : uint
{
    None = 0,
    Js = 1 << 0,          // emit js file
    JsMap = 1 << 1,       // emit js.map file
    JsInlineMap = 1 << 2, // emit inline source map in js file
    DtsErrors = 1 << 3,   // emit dts errors
    DtsEmit = 1 << 4,     // emit d.ts file
    DtsMap = 1 << 5,      // emit d.ts.map file

    Dts = DtsErrors | DtsEmit,
    AllJs = Js | JsMap | JsInlineMap,
    AllDtsEmit = DtsEmit | DtsMap,
    AllDts = Dts | DtsMap,
    All = AllJs | AllDts,
}

public static class FileEmitKindHelpers
{
    // snapshot.go:59-77
    public static FileEmitKind GetFileEmitKind(CompilerOptions options)
    {
        var result = FileEmitKind.Js;
        if (options.SourceMap.IsTrue())
        {
            result |= FileEmitKind.JsMap;
        }
        if (options.InlineSourceMap.IsTrue())
        {
            result |= FileEmitKind.JsInlineMap;
        }
        if (options.GetEmitDeclarations())
        {
            result |= FileEmitKind.Dts;
        }
        if (options.DeclarationMap.IsTrue())
        {
            result |= FileEmitKind.DtsMap;
        }
        if (options.EmitDeclarationOnly.IsTrue())
        {
            result &= FileEmitKind.AllDts;
        }
        return result;
    }

    // snapshot.go:79-83
    internal static FileEmitKind GetPendingEmitKindWithOptions(CompilerOptions options, CompilerOptions oldOptions)
    {
        var oldEmitKind = GetFileEmitKind(oldOptions);
        var newEmitKind = GetFileEmitKind(options);
        return GetPendingEmitKind(newEmitKind, oldEmitKind);
    }

    // snapshot.go:85-107
    internal static FileEmitKind GetPendingEmitKind(FileEmitKind emitKind, FileEmitKind oldEmitKind)
    {
        if (oldEmitKind == emitKind)
        {
            return FileEmitKind.None;
        }
        if (oldEmitKind == 0 || emitKind == 0)
        {
            return emitKind;
        }
        var diff = oldEmitKind ^ emitKind;
        var result = FileEmitKind.None;
        // If there is diff in Js emit, pending emit is js emit flags
        if ((diff & FileEmitKind.AllJs) != 0)
        {
            result |= emitKind & FileEmitKind.AllJs;
        }
        // If dts errors pending, add dts errors flag
        if ((diff & FileEmitKind.DtsErrors) != 0)
        {
            result |= emitKind & FileEmitKind.AllDts;
        }
        // If there is diff in Dts emit, pending emit is dts emit flags
        if ((diff & FileEmitKind.AllDtsEmit) != 0)
        {
            result |= emitKind & FileEmitKind.AllDtsEmit;
        }
        return result;
    }
}

// snapshot.go:109-131 — Signature (Hash of d.ts emitted), is string if it was emitted using the
// same d.ts.map option as what compilerOptions indicate, otherwise tuple of string.
internal sealed class EmitSignature
{
    internal string signature = "";
    internal string[]? signatureWithDifferentOptions;

    // snapshot.go:116-131 — Convert to Emit signature based on oldOptions and EmitSignature format.
    // If d.ts map options differ then swap the format, otherwise use as is.
    internal EmitSignature GetNewEmitSignature(CompilerOptions oldOptions, CompilerOptions newOptions)
    {
        if (oldOptions.DeclarationMap.IsTrue() == newOptions.DeclarationMap.IsTrue())
        {
            return this;
        }
        if (signatureWithDifferentOptions == null)
        {
            return new EmitSignature
            {
                signatureWithDifferentOptions = [signature],
            };
        }
        else
        {
            return new EmitSignature
            {
                signature = signatureWithDifferentOptions[0],
            };
        }
    }
}
