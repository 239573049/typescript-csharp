// Ported from tsgo internal/core/parsedoptions.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

namespace Ts.Core;

// PORT-NOTE: Go `*CompilerOptions`/`*WatchOptions`/`*TypeAcquisition` pointers → nullable
// references (nil-able). Go `[]string`/`[]*ProjectReference` → nullable arrays, preserving
// nil (05-conventions.md §6.1). Field order matches Go. C# permits a field whose name
// equals its type (Color-Color rule), so the Go field names are kept verbatim.
public sealed class ParsedOptions
{
    public CompilerOptions? CompilerOptions;
    public WatchOptions? WatchOptions;
    public TypeAcquisition? TypeAcquisition;

    public string[]? FileNames;
    public ProjectReference[]? ProjectReferences;
}
