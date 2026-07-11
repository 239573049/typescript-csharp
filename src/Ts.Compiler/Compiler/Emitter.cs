// Ported from tsgo internal/compiler/emitter.go.
//
// PORT-NOTE (phase 4/5 skeleton): the real emit path depends on the printer,
// transformers, declarations, and sourcemap packages, all of which are phase 6 and
// not yet ported. This file provides the emit data contracts (EmitOnly, EmitResult,
// EmitOptions, WriteFile*, SourceMapEmitResult, CombineEmitResults) plus the emitter
// orchestration shell. The actual transform/print steps throw NotImplementedException
// until phase 6 lands. Program.Emit is likewise stubbed (see Program.cs).
//
// §1.5 (namespace shadowing): cross-package class names equal to a sibling namespace
// segment are fully qualified with global::.

using System;
using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Compiler;

// emitter.go:24-31
public enum EmitOnly : byte
{
    All = 0,
    Js,
    Dts,
    ForcedDts,
}

// program.go:1605-1610
public sealed class WriteFileData
{
    public int SourceMapUrlPos;
    // PORT-NOTE: Go `BuildInfo any` -> object? (build-info type lands with the incremental wave).
    public object? BuildInfo;
    public Diagnostic[]? Diagnostics;
    public bool SkippedDtsWrite;
}

// program.go:1612
public delegate void WriteFile(string fileName, string text, WriteFileData? data);

// program.go:1614-1618
public sealed class EmitOptions
{
    public SourceFile? TargetSourceFile; // Single file to emit. If null, emits all files
    public EmitOnly EmitOnly;
    public WriteFile? WriteFile;
}

// program.go:1620-1625
public sealed class EmitResult
{
    public bool EmitSkipped;
    public Diagnostic[]? Diagnostics; // Contains declaration emit diagnostics
    public string[]? EmittedFiles; // Array of files the compiler wrote to disk
    public SourceMapEmitResult[]? SourceMaps; // Array of sourceMapData if compiler emitted sourcemaps
}

// program.go:1627-1631
public sealed class SourceMapEmitResult
{
    public string[]? InputSourceFileNames;
    // PORT-NOTE: Go `*sourcemap.RawSourceMap` -> object? until the sourcemap package is ported (phase 6).
    public object? SourceMap;
    public string GeneratedFile = "";
}

// program.go:1697-1713
public static class EmitResults
{
    public static EmitResult CombineEmitResults(IReadOnlyList<EmitResult?> results)
    {
        var result = new EmitResult();
        var diagnostics = new List<Diagnostic>();
        var emittedFiles = new List<string>();
        var sourceMaps = new List<SourceMapEmitResult>();
        foreach (var emitResult in results)
        {
            if (emitResult is null)
            {
                continue; // Skip nil results
            }
            if (emitResult.EmitSkipped)
            {
                result.EmitSkipped = true;
            }
            if (emitResult.Diagnostics is not null)
            {
                diagnostics.AddRange(emitResult.Diagnostics);
            }
            if (emitResult.EmittedFiles is not null)
            {
                emittedFiles.AddRange(emitResult.EmittedFiles);
            }
            if (emitResult.SourceMaps is not null)
            {
                sourceMaps.AddRange(emitResult.SourceMaps);
            }
        }
        result.Diagnostics = diagnostics.Count > 0 ? diagnostics.ToArray() : null;
        result.EmittedFiles = emittedFiles.Count > 0 ? emittedFiles.ToArray() : null;
        result.SourceMaps = sourceMaps.Count > 0 ? sourceMaps.ToArray() : null;
        return result;
    }
}

// emitter.go:33-52
// PORT-NOTE: skeleton. The writer/paths/host fields and the transform+print pipeline
// depend on phase-6 packages (printer, transformers, declarations, outputpaths emit).
// emit() throws until those land; the orchestration in program.go's Emit is stubbed too.
internal sealed class Emitter
{
    internal EmitOnly EmitOnly;
    internal SourceFile? SourceFile;
    internal WriteFile? WriteFile;
    internal EmitResult EmitResult = new();

    internal void Emit()
    {
        // emitter.go:45-52 — real body runs script/declaration transformers and the printer.
        throw new NotImplementedException("compiler emit is a phase 6 deliverable (printer/transformers/declarations not yet ported)");
    }
}
