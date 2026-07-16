// Ported from tsgo internal/compiler/program.go (Emit / getSourceFilesToEmit / HandleNoEmitOnError /
// IsEmitBlocked) and internal/compiler/emitHost.go (EmitHost interface + emitHost struct / newEmitHost).
//
// This wave wires Program.Emit: build the per-file emitters, construct an EmitHost bound to the
// per-file checker's EmitResolver, run the emit pipeline (Emitter.cs), and combine results.
//
// §1.5 (namespace shadowing): sibling-namespace class names are fully qualified with `global::`
// (printer → global::Ts.Printer.*, transformers.declarations → global::Ts.Transformers.Declarations.*,
// outputpaths → global::Ts.OutputPaths.*). Host classes with no namespace collision
// (AstUtilities, EmitResults, CoreUtil) are used unqualified.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.Tracing;

namespace Ts.Compiler;

// emitHost.go:19-28 — combines printer.EmitHost + declarations.DeclarationEmitHost + the compiler
// extras. Ported as a compiler-local interface aggregating the two consumer contracts.
// NOTE: EmitHost operations must be thread-safe.
public interface EmitHost :
    global::Ts.Printer.EmitHost,
    global::Ts.Transformers.Declarations.IDeclarationEmitHost,
    global::Ts.OutputPaths.OutputPathsHost
{
    // The three base interfaces declare identically-shaped members (GetCurrentDirectory /
    // UseCaseSensitiveFileNames on printer.EmitHost + IDeclarationEmitHost; CommonSourceDirectory on
    // printer.EmitHost + OutputPathsHost; GetEmitResolver on printer.EmitHost + IDeclarationEmitHost).
    // Re-declaring them here with `new` unifies the inherited signatures so calls through EmitHost are
    // unambiguous (CS0121). Shapes are identical, so a single implementation satisfies all bases.
    new string GetCurrentDirectory();
    new bool UseCaseSensitiveFileNames();
    new string CommonSourceDirectory();
    new global::Ts.Printer.EmitResolver GetEmitResolver();
}

// emitHost.go:32-139 — emitHost struct. Bound to a per-file checker's EmitResolver (thread-safe).
internal sealed class EmitHostImpl : EmitHost
{
    private readonly Program program;
    private readonly global::Ts.Printer.EmitResolver emitResolver;

    private EmitHostImpl(Program program, global::Ts.Printer.EmitResolver emitResolver)
    {
        this.program = program;
        this.emitResolver = emitResolver;
    }

    // emitHost.go:38-44 — newEmitHost. Returns (host, done); `done` releases the checker back to the pool.
    internal static (EmitHostImpl Host, Action Release) NewEmitHost(CancellationToken ctx, Program program, SourceFile file)
    {
        var (checker, done) = program.GetTypeCheckerForFile(ctx, file);
        return (new EmitHostImpl(program, checker.GetEmitResolver()), done);
    }

    // emitHost.go:107 / :108 / :109 / :110
    public CompilerOptions? Options() => program.Options();
    public SourceFile[] SourceFiles() => program.SourceFiles();
    public string GetCurrentDirectory() => program.GetCurrentDirectory();
    public string CommonSourceDirectory() => program.CommonSourceDirectory();

    // emitHost.go:112-114
    public bool UseCaseSensitiveFileNames() => program.UseCaseSensitiveFileNames();

    // emitHost.go:116-118
    public bool IsEmitBlocked(string file) => program.IsEmitBlocked(file);

    // emitHost.go:120-122 — Go returns error; C# host FS throws IOException. Faithful to
    // printer.EmitHost.WriteFile which is modeled as throwing (05-conventions.md §4.1).
    public void WriteFile(string fileName, string text) => program.Host().FS().WriteFile(fileName, text);

    // emitHost.go:58-60
    public ModuleKind GetEmitModuleFormatOfFile(HasFileName file) => program.GetEmitModuleFormatOfFile(file);

    // emitHost.go:124-126
    public global::Ts.Printer.EmitResolver GetEmitResolver() => emitResolver;

    // emitHost.go:82-84
    public global::Ts.TsOptions.SourceOutputAndProjectReference? GetProjectReferenceFromSource(global::Ts.TsPath.Path path) =>
        program.GetProjectReferenceFromSource(path);

    // emitHost.go:128-130
    public bool IsSourceFileFromExternalLibrary(SourceFile file) => program.IsSourceFileFromExternalLibrary(file);

    // ── declarations.IDeclarationEmitHost ────────────────────────────────────────────────────────

    // emitHost.go:90-92
    public ModifierFlags GetEffectiveDeclarationFlags(Node node, ModifierFlags flags) =>
        emitResolver.GetEffectiveDeclarationFlags(node, flags);

    // emitHost.go:99-101
    public ResolutionMode GetResolutionModeOverride(Node node) => emitResolver.GetResolutionModeOverride(node);

    // emitHost.go:94-97
    public global::Ts.Transformers.Declarations.IOutputPaths GetOutputPathsFor(SourceFile file, bool forceDtsPaths) =>
        new OutputPathsAdapter(global::Ts.OutputPaths.OutputPaths.GetOutputPathsFor(file, Options()!, this, forceDtsPaths));

    // emitHost.go:103-105 — delegates to the program (declaration emit's triple-slash reference emit).
    public SourceFile? GetSourceFileFromReference(SourceFile origin, FileReference reference) =>
        program.GetSourceFileFromReference(origin, reference);

    // Adapts the concrete outputpaths.OutputPaths to declarations.IOutputPaths (the interface lives in
    // the declarations package; OutputPaths cannot declare it without a cross-package reference).
    private sealed class OutputPathsAdapter(global::Ts.OutputPaths.OutputPaths paths) : global::Ts.Transformers.Declarations.IOutputPaths
    {
        public string DeclarationFilePath() => paths.DeclarationFilePath();
        public string JsFilePath() => paths.JsFilePath();
    }
}

public partial class Program
{
    // program.go:100 — populated by BlockEmittingOfFile (verifyCompilerOptions emit-path checks).
    private readonly global::Ts.Collections.Set<global::Ts.TsPath.Path> hasEmitBlockingDiagnostics = new();

    // program.go:1230-1232
    public bool IsEmitBlocked(string emitFileName) =>
        hasEmitBlockingDiagnostics.Has(global::Ts.TsPath.TsPath.ToPath(emitFileName, GetCurrentDirectory(), UseCaseSensitiveFileNames()));

    // program.go:719-727 — cached getSourceFilesToEmit for the all-files, non-force path.
    // PORT-NOTE: the sync.Once cache is elided; the filter is cheap and recomputed. Matches Go's
    // observable output (order-preserving Filter over SourceFiles()).
    private SourceFile[] GetSourceFilesToEmit(SourceFile? targetSourceFile, bool forceDtsEmit) =>
        Emitter_GetSourceFilesToEmit(targetSourceFile, forceDtsEmit);

    // emitter.go:505-515 — getSourceFilesToEmit(host, target, forceDts).
    private SourceFile[] Emitter_GetSourceFilesToEmit(SourceFile? targetSourceFile, bool forceDtsEmit)
    {
        SourceFile[] sourceFiles = targetSourceFile is not null ? [targetSourceFile] : SourceFiles();
        var result = new List<SourceFile>();
        foreach (var sourceFile in sourceFiles)
        {
            if (SourceFileMayBeEmitted(sourceFile, forceDtsEmit))
            {
                result.Add(sourceFile);
            }
        }
        return result.ToArray();
    }

    // program.go:1633-1695 — Emit.
    public EmitResult Emit(CancellationToken ctx, EmitOptions options)
    {
        Action releaseTracing = opts.Tracing.Push(global::Ts.Tracing.Phase.Emit, "emit", null, true);
        try
        {
            if (options.EmitOnly != EmitOnly.ForcedDts)
            {
                var result = HandleNoEmitOnError(ctx, this, options.TargetSourceFile);
                if (result is not null || ctx.IsCancellationRequested)
                {
                    return result ?? new EmitResult { EmitSkipped = true };
                }
            }

            var newLine = Options().NewLine.GetNewLineCharacter();

            // PORT-NOTE: Go uses a sync.Pool of EmitTextWriter shared across the WorkGroup. C# uses a
            // per-emitter writer (allocated in the queued work); functionally equivalent (each writer is
            // used by exactly one file), and avoids the pool machinery for this wave.
            var wg = WorkGroup.Create(SingleThreaded());
            var emitters = new List<Emitter>();
            var sourceFiles = GetSourceFilesToEmit(options.TargetSourceFile, options.EmitOnly == EmitOnly.ForcedDts);

            foreach (var sourceFile in sourceFiles)
            {
                var emitter = new Emitter
                {
                    writer = null!,
                    sourceFile = sourceFile,
                    emitOnly = options.EmitOnly,
                    writeFile = options.WriteFile,
                    tr = opts.Tracing,
                };
                emitters.Add(emitter);
                wg.Queue(() =>
                {
                    var (host, done) = EmitHostImpl.NewEmitHost(ctx, this, sourceFile);
                    try
                    {
                        emitter.host = host;

                        // take a fresh writer
                        var writer = global::Ts.Printer.TextWriter.NewTextWriter(newLine, 0);
                        writer.Clear();

                        // attach writer and perform emit
                        emitter.writer = writer;
                        emitter.paths = global::Ts.OutputPaths.OutputPaths.GetOutputPathsFor(sourceFile, host.Options()!, host, options.EmitOnly == EmitOnly.ForcedDts);
                        emitter.Emit();
                        emitter.writer = null!;
                    }
                    finally
                    {
                        done();
                    }
                });
            }

            // wait for emit to complete
            wg.RunAndWait();

            // collect results from emit, preserving input order
            var results = new List<EmitResult?>(emitters.Count);
            foreach (var e in emitters)
            {
                results.Add(e.emitResult);
            }
            return EmitResults.CombineEmitResults(results);
        }
        finally
        {
            releaseTracing();
        }
    }

    // program.go:1733-1753 — HandleNoEmitOnError. Static because the CLI path calls it via ProgramLike;
    // here it is bound to Program directly (the only ProgramLike implementation in this wave).
    public static EmitResult? HandleNoEmitOnError(CancellationToken ctx, Program program, SourceFile? file)
    {
        if (!program.Options().NoEmitOnError.IsTrue())
        {
            return null; // No emit on error is not set, so we can proceed with emitting
        }

        var diagnostics = GetDiagnosticsOfAnyProgram(
            ctx,
            program,
            file,
            true,
            program.GetBindDiagnostics,
            program.GetSemanticDiagnostics);
        if (diagnostics.Length == 0)
        {
            return null; // No diagnostics, so we can proceed with emitting
        }
        return new EmitResult
        {
            Diagnostics = diagnostics,
            EmitSkipped = true,
        };
    }
}
