// plan/05-conventions.md §2.3: pure Go type aliases (`type X = Y`) become global
// usings, collected in this project-level GlobalUsings.cs.

// Go: internal/stringutil/compare.go:26 `type Comparison = int`
global using Comparison = int;

// Go: internal/core/compileroptions.go:421 `type ResolutionMode = ModuleKind`
// (ModuleKindNone | ModuleKindCommonJS | ModuleKindESNext).
global using ResolutionMode = Ts.Core.ModuleKind;

// Go: internal/ast/symbol.go:45 `type SymbolTable map[string]*Symbol` (plan/05 §2.3).
// Ordinal string keys (C# default). Iteration-order discipline per plan/05 §7 is applied
// at each binder/checker iteration point.
global using SymbolTable = System.Collections.Generic.Dictionary<string, Ts.Ast.Symbol>;

// Go: internal/ast/flow.go:39 `type FlowLabel = FlowNode` (plan/03 §11.5).
global using FlowLabel = Ts.Ast.FlowNode;
