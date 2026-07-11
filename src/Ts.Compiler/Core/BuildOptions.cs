// Ported from tsgo internal/core/buildoptions.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

namespace Ts.Core;

// PORT-NOTE: Go's `_ noCopy` vet sentinel is dropped (C# classes are reference types).
// Field order matches Go declaration order. `*int` → `int?`.
public sealed class BuildOptions
{
    public Tristate Dry;
    public Tristate Force;
    public Tristate Verbose;
    public int? Builders;
    public Tristate StopBuildOnErrors;

    // CompilerOptions are not parsed here and will be available on ParsedBuildCommandLine

    // Internal fields
    public Tristate Clean;
}
