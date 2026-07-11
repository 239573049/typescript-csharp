using Ts.TsPath;

namespace Ts.Ast;

// Go: internal/ast/parseoptions.go — the option value types.
//
// SCOPE: the parseoptions.go functions (GetExternalModuleIndicatorOptions,
// SetExternalModuleIndicator, isFileProbablyExternalModule, walkTreeForJSXTags, …) depend
// on utilities.go predicates (HasSyntacticModifier, IsImportDeclaration, …) and
// GetImpliedNodeFormatForEmitWorker; they land with the utilities.go port (this phase,
// after those predicates exist).

// parseoptions.go:8-12
public struct SourceFileParseOptions
{
    public string FileName;
    public Path Path;
    public ExternalModuleIndicatorOptions ExternalModuleIndicatorOptions;
}

// parseoptions.go:14-17
public struct ExternalModuleIndicatorOptions
{
    public bool Jsx; // Go field JSX
    public bool Force;
}
