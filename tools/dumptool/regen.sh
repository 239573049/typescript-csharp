#!/usr/bin/env bash
# Regenerates the differential fixtures from a tsgo checkout by applying the
# dumptool patch branch, running the exporter over the corpus, packing shards,
# and updating fixtures/fixtures.lock.json.
#
# Phase-0 status: SKELETON. The structure, arguments, and step sequence are
# complete and `--help` / `--dry-run` run; the steps marked TODO(phase-2) are
# stubs that print what they will do. Full spec: plan/08-testing-strategy.md §2.6.
set -euo pipefail
export LC_ALL=C

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
readonly REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd -P)"
readonly LOCK_FILE="$REPO_ROOT/UPSTREAM.lock"
readonly FIXTURES_DIR="$REPO_ROOT/fixtures"
readonly FIXTURES_LOCK="$FIXTURES_DIR/fixtures.lock.json"
readonly PATCHES_DIR="$SCRIPT_DIR/patches"

TSGO_ROOT="${TSGO_ROOT:-/Users/token/Desktop/code/typescript-go-main}"
DRY_RUN=0
FAMILIES="tokens,rescan,ast,parsediag,symbols,flow"

usage() {
    cat >&2 <<'EOF'
usage: tools/dumptool/regen.sh [options]

Regenerate the differential fixtures from a tsgo checkout.

options:
  --tsgo <dir>        tsgo checkout to patch and export from
                      (default: $TSGO_ROOT or /Users/token/Desktop/code/typescript-go-main)
  --families <list>   comma-separated subset of tokens,rescan,ast,parsediag,symbols,flow
                      (default: all)
  --dry-run           print each step without applying patches or writing fixtures
  --help              show this help

The tsgo checkout must be at the commit recorded in UPSTREAM.lock (tsgo.commit).
EOF
    exit "${1:-2}"
}

log()  { echo "regen: $*" >&2; }
step() { echo; echo "==> $*" >&2; }

lock_value() {
    grep -E "^${1}[[:space:]]*=" "$LOCK_FILE" | head -1 | sed -E 's/^[^=]*=[[:space:]]*//'
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --tsgo)     TSGO_ROOT="$2"; shift 2 ;;
        --families) FAMILIES="$2"; shift 2 ;;
        --dry-run)  DRY_RUN=1; shift ;;
        --help|-h)  usage 0 ;;
        *)          echo "regen: unknown argument: $1" >&2; usage 2 ;;
    esac
done

readonly PINNED_COMMIT="$(lock_value tsgo.commit)"
run() { if [[ "$DRY_RUN" == 1 ]]; then echo "  [dry-run] $*" >&2; else "$@"; fi; }

step "0/5  preflight"
log "tsgo checkout : $TSGO_ROOT"
log "pinned commit : $PINNED_COMMIT"
log "families      : $FAMILIES"
[[ -d "$TSGO_ROOT" ]] || { log "tsgo checkout not found"; exit 1; }

step "1/5  apply dumptool patch branch"
# TODO(phase-2): git -C "$TSGO_ROOT" am "$PATCHES_DIR"/*.patch
if compgen -G "$PATCHES_DIR/*.patch" >/dev/null; then
    run bash -c "git -C '$TSGO_ROOT' am '$PATCHES_DIR'/*.patch"
else
    log "no patches yet (skeleton) — exporter not built; nothing to apply"
fi

step "2/5  export fixtures from corpus"
# TODO(phase-2): (cd "$TSGO_ROOT" && go run ./cmd/dumptool -families "$FAMILIES" -out "$TMP_OUT")
log "TODO(phase-2): run cmd/dumptool over corpus into a temp output dir"

step "3/5  pack shards (reproducible tar.zst)"
# TODO(phase-2): node "$SCRIPT_DIR/pack.ts" --in "$TMP_OUT" --out "$FIXTURES_DIR"
log "TODO(phase-2): sort entries by codepoint, zero mtimes, zstd tar per family×dir"

step "4/5  update fixtures.lock.json"
# TODO(phase-2): recompute per-shard xxh3 and rewrite families table; keep tsgoCommit == pin
log "TODO(phase-2): rewrite $FIXTURES_LOCK families table with shard hashes"
[[ -f "$FIXTURES_LOCK" ]] || { log "fixtures.lock.json missing"; exit 1; }

step "5/5  done"
log "skeleton run complete (no fixtures written)"
