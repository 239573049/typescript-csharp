# dumptool — tsgo patch branch

`dumptool` is the Go-side oracle for the differential fixtures consumed in phases
2–4 (token streams, AST structure, parse diagnostics, symbol tables, flow graphs).
Full specification: [plan/08-testing-strategy.md](../../../plan/08-testing-strategy.md) §2.

## Why a patch branch

Go's `internal/` visibility rule means anything that imports `internal/scanner`,
`internal/parser`, `internal/binder`, `internal/ast` must live **inside the tsgo
module tree**. So `dumptool` is a set of `git format-patch` products applied on top
of the pinned tsgo commit recorded in [UPSTREAM.lock](../../../UPSTREAM.lock)
(`tsgo.commit`).

## Discipline P1 — add-only, never modify

Every patch here adds files only, under two new directories:

- `cmd/dumptool/` — the CLI entry point
- `internal/dumptool/` — the six family exporters

tsgo's existing exported surface is sufficient (see §2.1 for the exact symbols),
so no existing file needs to change. **If** a future family genuinely needs a hook
into tsgo internals, keep the diff minimal, put it in its own separate patch file,
and register the reason in the table below. Add-only keeps the upstream-bump rebase
(00-overview.md upgrade flow, step 5) near-zero-cost.

## Applying

```sh
cd "$TSGO_ROOT"                       # the tsgo checkout at UPSTREAM.lock tsgo.commit
git checkout -b dumptool
git am /path/to/tscs/tools/dumptool/patches/*.patch
```

## Patch registry

| Patch | Adds | Modifies existing? | Reason (required only if it modifies) |
|---|---|---|---|
| _(none yet — skeleton)_ | | | |

> Status: **skeleton (phase 0, W0-22)**. The actual exporter patches are authored
> when phase 2 begins (they must match the fixture format frozen in
> plan/08 §2.3). This directory currently holds only this README and the regen
> harness so the pipeline structure is reviewable and `regen.sh --help` runs.
