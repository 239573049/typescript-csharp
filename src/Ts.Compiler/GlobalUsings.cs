// plan/05-conventions.md §2.3: pure Go type aliases (`type X = Y`) become global
// usings, collected in this project-level GlobalUsings.cs.

// Go: internal/stringutil/compare.go:26 `type Comparison = int`
global using Comparison = int;

// Go: internal/core/compileroptions.go:421 `type ResolutionMode = ModuleKind`
// (ModuleKindNone | ModuleKindCommonJS | ModuleKindESNext).
global using ResolutionMode = Ts.Core.ModuleKind;
