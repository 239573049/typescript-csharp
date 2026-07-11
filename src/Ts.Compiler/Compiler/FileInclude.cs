// Ported from tsgo internal/compiler/fileInclude.go.
//
// The loader populates FileIncludeReason / referencedFileData / automaticTypeDirectiveFileData
// as plain data during file loading; those data shapes and the O(1) accessors are ported real.
//
// PORT-NOTE (phase 4/5): the diagnostic *computation* methods (toDiagnostic, computeDiagnostic,
// getReferencedLocation, toRelatedInfo, referenceFileLocation.text/diagnosticAt) are part of the
// "explain why a file is in the program" diagnostic surface. That surface is deferred with the
// rest of program diagnostics; these methods throw NotImplementedException until it lands. The
// load pipeline never invokes them.
//
// §1.5 (namespace shadowing): cross-package class names equal to a sibling namespace segment
// are fully qualified with global::.

using System;
using Ts.Ast;

namespace Ts.Compiler;

// fileInclude.go:15-27
internal enum FileIncludeKind
{
    // References from file
    Import = 0,
    ReferenceFile,
    TypeReferenceDirective,
    LibReferenceDirective,

    RootFile,
    LibFile,
    AutomaticTypeDirectiveFile,
}

// fileInclude.go:29-40
public sealed class FileIncludeReason
{
    internal FileIncludeKind Kind;
    internal object? Data;

    // Uses relative file name (lazily computed diagnostic)
    internal Diagnostic? RelativeFileNameDiag;
    internal readonly Once RelativeFileNameDiagOnce = new();

    // Uses file name as is
    internal Diagnostic? Diag;
    internal readonly Once DiagOnce = new();

    // fileInclude.go:81-83
    internal int AsIndex() => (int)Data!;

    // fileInclude.go:85-88
    internal bool AsLibFileIndex(out int index)
    {
        if (Data is int i)
        {
            index = i;
            return true;
        }
        index = 0;
        return false;
    }

    // fileInclude.go:90-92
    internal bool IsReferencedFile() => Kind <= FileIncludeKind.LibReferenceDirective;

    // fileInclude.go:94-96
    internal ReferencedFileData AsReferencedFileData() => (ReferencedFileData)Data!;

    // fileInclude.go:98-100
    internal AutomaticTypeDirectiveFileData AsAutomaticTypeDirectiveFileData() => (AutomaticTypeDirectiveFileData)Data!;

    // fileInclude.go:102-151 — diagnostic surface, deferred.
    internal ReferenceFileLocation GetReferencedLocation(Program program) =>
        throw new NotImplementedException("file-include diagnostic explanation is deferred (phase 4/5 skeleton)");

    // fileInclude.go:153-167 — diagnostic surface, deferred.
    internal Diagnostic ToDiagnostic(Program program, bool relativeFileName) =>
        throw new NotImplementedException("file-include diagnostic explanation is deferred (phase 4/5 skeleton)");

    // fileInclude.go:259-300 — diagnostic surface, deferred.
    internal Diagnostic? ToRelatedInfo(Program program) =>
        throw new NotImplementedException("file-include diagnostic explanation is deferred (phase 4/5 skeleton)");
}

// fileInclude.go:42-46
internal sealed class ReferencedFileData
{
    internal global::Ts.TsPath.Path File;
    internal int Index;
    internal Node? Synthetic;
}

// fileInclude.go:48-54
internal sealed class ReferenceFileLocation
{
    internal SourceFile File = null!;
    internal Node? Node;
    internal FileReference? Ref;
    internal global::Ts.Module.PackageId PackageId;
    internal bool IsSynthetic;

    // fileInclude.go:56-74 — diagnostic surface, deferred.
    internal string Text() =>
        throw new NotImplementedException("file-include diagnostic explanation is deferred (phase 4/5 skeleton)");

    internal Diagnostic DiagnosticAt(global::Ts.Diagnostics.Message message, params object?[] args) =>
        throw new NotImplementedException("file-include diagnostic explanation is deferred (phase 4/5 skeleton)");
}

// fileInclude.go:76-79
internal sealed class AutomaticTypeDirectiveFileData
{
    internal string TypeReference = "";
    internal global::Ts.Module.PackageId PackageId;

    internal AutomaticTypeDirectiveFileData()
    {
    }

    internal AutomaticTypeDirectiveFileData(string typeReference, global::Ts.Module.PackageId packageId)
    {
        TypeReference = typeReference;
        PackageId = packageId;
    }
}
