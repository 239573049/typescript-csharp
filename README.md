# typescript-csharp

C#/.NET 10 (Native AOT) port of [typescript-go](https://github.com/microsoft/typescript-go) — a 1:1
translation of the Go implementation of the TypeScript compiler: same rules, same algorithms, same
architecture, verified byte-for-byte against the official conformance baselines.

**Read the migration plan first: [plan/00-overview.md](plan/00-overview.md).** The `plan/` directory
is the project's source of truth for scope, architecture mapping, phases, gates, conventions, and risks.

## Layout

| Path | Contents |
|---|---|
| `plan/` | The full migration plan (13 documents) |
| `src/Ts.Compiler/` | Compiler core: core…scanner/parser/binder/checker/printer/transformers (port of `internal/*`) |
| `src/Ts.LanguageService/` | ls/lsp/project/api/format/fswatch/jsonrpc |
| `src/Ts.Cli/` | The tsgo-equivalent single binary `tscs` (tsc CLI, lsp, api modes), `PublishAot` |
| `tests/` | xUnit v3 test projects incl. the conformance baseline runner |
| `tools/generator/` | TypeScript-based code generators (AST, diagnostics, LSP protocol, libs) |
| `testdata/` | Baselines synced from typescript-go (added in phase 3) |
| `_submodules/TypeScript` | Official TypeScript repo pinned to typescript-go's commit (added in phase 0/3) |

## Build

```sh
dotnet build TypeScript.slnx          # everything
dotnet test  TypeScript.slnx          # unit tests (JIT)
dotnet publish src/Ts.Cli/Ts.Cli.csproj -c Release -r osx-arm64   # Native AOT binary
```

Requires .NET SDK 10.0.3xx.

## Upstream pinning

The port tracks a fixed commit of typescript-go (and its TypeScript submodule pin). See
[plan/00-overview.md](plan/00-overview.md) for the sync/rebase procedure and
[plan/11-progress.md](plan/11-progress.md) for current status.
