// Ported from tsgo internal/compiler/processingDiagnostic.go.
//
// The loader/filesParser create processingDiagnostic + includeExplainingDiagnostic as plain
// data during loading; toDiagnostic / createDiagnosticExplainingFile materialize that data into
// user-facing diagnostics (include-processor diagnostics wave).
//
// §1.5 (namespace shadowing): cross-package class names equal to a sibling namespace segment
// are fully qualified with global::.

using System;
using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Compiler;

// processingDiagnostic.go:15-20
internal enum ProcessingDiagnosticKind
{
    UnknownReference = 0,
    ExplainingFileInclude,
}

// processingDiagnostic.go:22-25
internal sealed class ProcessingDiagnostic
{
    internal ProcessingDiagnosticKind Kind;
    internal object? Data;

    // processingDiagnostic.go:27-29
    internal FileIncludeReason AsFileIncludeReason() => (FileIncludeReason)Data!;

    // processingDiagnostic.go:38-40
    internal IncludeExplainingDiagnostic AsIncludeExplainingDiagnostic() => (IncludeExplainingDiagnostic)Data!;

    // processingDiagnostic.go:42-67
    internal Diagnostic ToDiagnostic(Program program)
    {
        switch (Kind)
        {
            case ProcessingDiagnosticKind.UnknownReference:
            {
                var reference = AsFileIncludeReason();
                var loc = reference.GetReferencedLocation(program);
                switch (reference.Kind)
                {
                    case FileIncludeKind.TypeReferenceDirective:
                        return loc.DiagnosticAt(global::Ts.Diagnostics.Diagnostics.Cannot_find_type_definition_file_for_0, loc.Ref!.Value.FileName);
                    case FileIncludeKind.LibReferenceDirective:
                    {
                        var libName = global::Ts.TsPath.TsPath.ToFileNameLowerCase(loc.Ref!.Value.FileName);
                        var unqualifiedLibName = TrimSuffix(TrimPrefix(libName, "lib."), ".d.ts");
                        var suggestion = global::Ts.Core.CoreUtil.GetSpellingSuggestionForStrings(unqualifiedLibName, global::Ts.TsOptions.EnumMaps.Libs);
                        return loc.DiagnosticAt(
                            suggestion != ""
                                ? global::Ts.Diagnostics.Diagnostics.Cannot_find_lib_definition_for_0_Did_you_mean_1
                                : global::Ts.Diagnostics.Diagnostics.Cannot_find_lib_definition_for_0,
                            libName, suggestion);
                    }
                    default:
                        throw new InvalidOperationException("unknown include kind");
                }
            }
            case ProcessingDiagnosticKind.ExplainingFileInclude:
                return CreateDiagnosticExplainingFile(program);
            default:
                throw new InvalidOperationException("unknown processingDiagnosticKind");
        }
    }

    // processingDiagnostic.go:69-135
    private Diagnostic CreateDiagnosticExplainingFile(Program program)
    {
        var diag = AsIncludeExplainingDiagnostic();
        List<Diagnostic>? includeDetails = null;
        List<Diagnostic>? relatedInfo = null;
        Diagnostic[]? redirectInfo = null;
        FileIncludeReason? preferredLocation = null;
        var seenReasons = new HashSet<FileIncludeReason>();
        if (diag.DiagnosticReason.IsReferencedFile() && !program.IncludeProcessorInstance.GetReferenceLocation(diag.DiagnosticReason!, program).IsSynthetic)
        {
            preferredLocation = diag.DiagnosticReason;
        }

        void ProcessRelatedInfo(FileIncludeReason includeReason)
        {
            if (preferredLocation is null && includeReason.IsReferencedFile() && !program.IncludeProcessorInstance.GetReferenceLocation(includeReason, program).IsSynthetic)
            {
                preferredLocation = includeReason;
            }
            else if (preferredLocation != includeReason)
            {
                var info = program.IncludeProcessorInstance.GetRelatedInfo(includeReason, program);
                if (info is not null)
                {
                    (relatedInfo ??= []).Add(info);
                }
            }
        }

        void ProcessInclude(FileIncludeReason includeReason)
        {
            if (!seenReasons.Add(includeReason))
            {
                return;
            }
            (includeDetails ??= []).Add(includeReason.ToDiagnostic(program, false));
            ProcessRelatedInfo(includeReason);
        }

        // !!! todo sheetal caching

        // PORT-NOTE: Go's `diag.file != ""` — the C# Path zero value carries a null Value; IsNullOrEmpty covers both.
        if (!string.IsNullOrEmpty(diag.File.Value))
        {
            var reasons = program.IncludeProcessorInstance.FileIncludeReasons.GetValueOrDefault(diag.File);
            includeDetails = new List<Diagnostic>(reasons?.Count ?? 0);
            foreach (var reason in reasons ?? [])
            {
                ProcessInclude(reason);
            }
            redirectInfo = program.IncludeProcessorInstance.ExplainRedirectAndImpliedFormat(program, diag.File, fileName => fileName);
        }
        if (diag.DiagnosticReason is not null)
        {
            ProcessInclude(diag.DiagnosticReason);
        }
        List<Diagnostic>? chain = null;
        if (includeDetails is not null && (preferredLocation is null || seenReasons.Count != 1))
        {
            var fileReason = Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.The_file_is_in_the_program_because_Colon);
            fileReason.SetMessageChain(includeDetails.ToArray());
            chain = [fileReason];
        }
        if (redirectInfo is not null)
        {
            (chain ??= []).AddRange(redirectInfo);
        }

        Diagnostic? result = null;
        if (preferredLocation is not null)
        {
            result = program.IncludeProcessorInstance.GetReferenceLocation(preferredLocation, program).DiagnosticAt(diag.Message!, diag.Args ?? []);
        }
        result ??= Diagnostic.NewCompilerDiagnostic(diag.Message!, diag.Args ?? []);
        if (chain is not null)
        {
            result.SetMessageChain(chain.ToArray());
        }
        if (relatedInfo is not null)
        {
            result.SetRelatedInfo(relatedInfo.ToArray());
        }
        return result;
    }

    // strings.TrimPrefix
    private static string TrimPrefix(string s, string prefix) =>
        s.StartsWith(prefix, StringComparison.Ordinal) ? s[prefix.Length..] : s;

    // strings.TrimSuffix
    private static string TrimSuffix(string s, string suffix) =>
        s.EndsWith(suffix, StringComparison.Ordinal) ? s[..^suffix.Length] : s;
}

// processingDiagnostic.go:31-36
internal sealed class IncludeExplainingDiagnostic
{
    internal global::Ts.TsPath.Path File;
    internal FileIncludeReason? DiagnosticReason;
    internal global::Ts.Diagnostics.Message? Message;
    internal object?[]? Args;
}
