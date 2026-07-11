# fixtures/

Self-authored test fixtures. Kept **out of `testdata/`** so that tree stays a
byte-for-byte tsgo mirror (ruling R14). Full spec:
[plan/08-testing-strategy.md](../plan/08-testing-strategy.md) §2.

## Contents

| Path | What | Storage |
|---|---|---|
| `stringutil/` | Byte-oracle copies of tsgo's generated Unicode tables (verify the transcribed `*.g.cs`) | plain git (small, ~300 KB) |
| `jsnum/` | jsnum four-way parity fixtures (added when G1.2 is wired) | plain git |
| `*.tar.zst` | Differential-fixture shards (tokens/rescan/ast/parsediag/symbols/flow), phases 2–4 | **Git LFS** |
| `fixtures.lock.json` | Shard manifest + `tsgoCommit` pin | plain git |

## Storage-channel decision (G0.5)

**Decided: Git LFS for the `*.tar.zst` shards** (plan §2.5 default). Rationale:
the packed differential corpus is estimated in the hundreds of MB; committing it
as ordinary Git objects would bloat every clone. Small text oracles
(`stringutil/`, `jsnum/`, the lock file) stay as plain Git objects.

`.gitattributes` already routes `fixtures/*.tar.zst` through LFS. No shards exist
yet (phase-0 skeleton), so `git lfs install` is deferred until phase 2 authors the
first shard — the attribute is harmless until then.

**Fallback** (plan §2.5, §11 open item 1): if the organization cannot use LFS,
switch to a separate `tscs-fixtures` repository referenced by commit. Trigger to
reconsider: any single shard exceeding ~50 MB compressed, or LFS quota exhaustion.

## Regenerating

`tools/dumptool/regen.sh` (see `tools/dumptool/patches/README.md`). Shards are
reproducible: entries sorted by codepoint, mtimes zeroed, so identical input →
identical bytes → stable shard hashes in `fixtures.lock.json`.
