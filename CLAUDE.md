# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this project is

A **1:1 port of [typescript-go](https://github.com/microsoft/typescript-go)** (Microsoft's official Go implementation of the TypeScript compiler, `tsgo`, ~300k LOC of production code) into **C#/.NET 10 Native AOT**. The goal is byte-for-byte identical output: same rules, same algorithms, same architecture. Acceptance is measured against the official conformance baselines.

Two documents govern all work — read them before doing anything non-trivial:
- **`AGENTS.md`** (in Chinese) — the working handbook: the per-session loop, the "iron rules," commit message format. It is the authoritative process guide.
- **`plan/00-overview.md` … `plan/12-risks.md`** (13 docs) — the single source of truth for scope, architecture, phase gates, conventions, and risks. **When code and plan conflict, the plan wins** (unless you go through the deviation-registration process in AGENTS.md §6). Authoritative-document routing table is in AGENTS.md §2.

The Go source being ported lives **outside this repo** at `/Users/token/Desktop/code/typescript-go-main` — treat it as **read-only reference**. The upstream commit is pinned in `UPSTREAM.lock` (only the upgrade PR may change it).

## Build, test, publish

```sh
dotnet build   TypeScript.slnx                                   # everything — must be 0 warnings, 0 errors
dotnet test    TypeScript.slnx                                   # unit + conformance (runs on JIT)
dotnet publish src/Ts.Cli/Ts.Cli.csproj -c Release -r osx-arm64  # Native AOT smoke test (run when touching src/)
```

Requires **.NET SDK 10.0.3xx**. `TreatWarningsAsErrors` is on globally, and trim/AOT/single-file analyzers plus BannedApiAnalyzers (`BannedSymbols.txt`) are all build-failing — a green build means analyzer-clean, not just compiling.

**Run a single test / filter:**
```sh
dotnet test tests/Ts.Compiler.Tests/Ts.Compiler.Tests.csproj                      # one project
dotnet test TypeScript.slnx --filter "FullyQualifiedName~Scanner"                  # by name substring
dotnet test tests/Ts.Conformance.Tests/Ts.Conformance.Tests.csproj --filter "..."  # conformance subset
```

**Conformance runner env vars** (see `tests/Ts.Conformance.Tests/`):
- `TSCS_SINGLE_THREADED=1` — force single-threaded checker; output **must** be byte-identical to the 4-checker parallel mode (a phase gate).
- `TSCS_CONFORMANCE_SHARD`, `TSCS_CONFORMANCE_WAVE_CAP` — split/limit the corpus.
- `TSCS_CONFORMANCE_UPDATE_FAILING`, `TSCS_CONFORMANCE_UPDATE_TYPES` — regenerate the `failingCases.txt` / `failingTypes.txt` allow-lists. **Baselines are never edited to make a test pass** — a baseline diff failing means your port is wrong.

## Code generation

`*.g.cs` files are **generated and must never be hand-edited** (a CI freshness check fails otherwise). The generators are TypeScript, run under Node ≥22.11 in `tools/generator/`:

```sh
cd tools/generator && npm ci
npm run gen:all          # ast, diagnostics, libs, lsp, stringers, encoder
npm run gen:ast          # (or an individual generator; see package.json scripts)
npm run check            # typecheck the generators (tsc)
```

To change generated output, edit the generator and regenerate; expected artifact counts live in `tools/generator/expected-counts.json`. The AST generates exactly **7 `.g.cs` files** (a locked decision in `plan/02`).

## Porting discipline (the rules that cause rework if broken)

These are condensed from `AGENTS.md §4` and `plan/05-conventions.md`; consult those for specifics.

- **Do not invent architecture.** Every design decision is already fixed in `plan/`. Your freedom is only in how to express a function's body in idiomatic C#. Go file → same-name C# file (PascalCase), function-by-function, **same function order**, Go comments carried over, `// !!!` markers preserved verbatim.
- **AOT bans are always in force** (`plan/07`): no reflection (except `typeof` / enum `ToString`), no reflection-based `System.Text.Json`, no culture-sensitive string APIs. The analyzers enforce these.
- **Positions are UTF-16 code units**, not bytes. Any "byte offset" concept ported from Go must be re-examined.
- **Every `for range m` (Go map iteration) port site must be annotated** `// map-iter: 序无关` (order-independent) or `// map-iter: 已排序` (sorted) — CI spot-checks this via regex.
- **`arena.New()` → `new T()`** (no pooling), *except* the specific pools named in `plan/01 §5.2`, which use `Ts.Core.ObjectPool<T>` (Microsoft.Extensions.ObjectPool is banned). Go single-threaded switch is `TSCS_SINGLE_THREADED`.
- Go nil-receiver methods → **extension methods**. `(T, error)` returns → one of nullable / `Try` pattern / exception per `plan/05 §4` (no fourth option).
- **Deep-recursion entry points must run through `LargeStackExecutor.Run`** (64 MB stack). Check when adding a new public entry point.
- Finish every session **green** (builds + tests). Rather leave a unit of work undone than leave a red repo.

**Commit messages:** `port(<pkg>): <go-file> → <cs-file>` for ports (e.g. `port(core): text.go → Text.cs`), `plan: …` for plan edits, `gen: …` for generator/generated changes. Do **not** push (no remote). Update `plan/11-progress.md` each session.

## Architecture map

Assembly layout (details in `plan/01-architecture.md`, package→file mapping in `plan/06-module-inventory.md`):

| Assembly | Ports | Notes |
|---|---|---|
| `src/Ts.Compiler` | `internal/*` — core, scanner, parser, binder, **checker**, printer, transformers, module resolution, VFS, diagnostics | The bulk of the compiler. Subdirs mirror Go packages (`Scanner/`, `Parser/`, `Binder/`, `Checker/`, …). |
| `src/Ts.LanguageService` | ls / lsp / project / format / jsonrpc | Language server. |
| `src/Ts.Cli` | the `tscs` binary | tsc CLI + lsp + api modes; this is what `PublishAot` produces. |

**Checker** (`plan/03-checker-design.md`) is Go's `checker.go` split into **22 `partial class Checker` files** (the split table in `plan/03 §4.2` is the sole authority for which function goes where). You'll see many `Checker.<Area>.cs` and paired `Checker.<Area>.Deps.cs` files — keep functions in their assigned partial. Types are ~24 structs; symbol/type links use `LinkStore` + interned caches; concurrency is deterministic (checker pool / `WorkGroup` / `LargeStackExecutor`).

**Threading model:** execution flows through `LargeStackExecutor` (64 MB stacks). The checker runs pooled and parallel (4 checkers) by default; `TSCS_SINGLE_THREADED=1` collapses it, and both modes must produce identical bytes.

## Test corpus & baselines

- `testdata/` is a **pure mirror** of tsgo's testdata (synced via `tools/sync-testdata.sh`, pinned in `UPSTREAM.lock`); never hand-edit it.
- `fixtures/` at the repo root holds **this project's own** fixtures.
- `_submodules/TypeScript` is the official TypeScript repo pinned to tsgo's TypeScript commit.
- Test projects use **xUnit v3**. `tests/Ts.TestUtil/` holds shared baseline/diff/fourslash/VFS helpers and `RepoPaths.cs`.
- Acceptance progresses by phase: token stream → AST dump → symbol/flow → `.errors.txt` → `.types`/`.symbols` → `.js`/`.d.ts`/`.js.map` → `.tsbuildinfo` → fourslash. Gate IDs and measurement commands are in `plan/09-phases.md`.
