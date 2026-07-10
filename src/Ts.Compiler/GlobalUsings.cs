// plan/05-conventions.md §2.3: pure Go type aliases (`type X = Y`) become global
// usings, collected in this project-level GlobalUsings.cs.

// Go: internal/stringutil/compare.go:26 `type Comparison = int`
global using Comparison = int;
