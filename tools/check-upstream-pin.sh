#!/usr/bin/env bash
# Verifies the upstream pin and that every committed generator product is fresh.
# Run by CI's pin-and-drift job (.github/workflows/ci.yml) and locally before an
# upstream bump. See 00-overview.md §4.4 and 04-codegen.md (drift check).
set -euo pipefail
export LC_ALL=C

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
readonly REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd -P)"
readonly LOCK_FILE="$REPO_ROOT/UPSTREAM.lock"
readonly GEN_DIR="$REPO_ROOT/tools/generator"

fail() { echo "check-upstream-pin: $*" >&2; exit 1; }

lock_value() {
    local key="$1" value
    value="$(grep -E "^${key}[[:space:]]*=" "$LOCK_FILE" | head -1 | sed -E 's/^[^=]*=[[:space:]]*//')" \
        || fail "key '$key' not found in UPSTREAM.lock"
    printf '%s' "$value"
}

sha256_of() {
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | awk '{print $1}'
    else
        sha256sum "$1" | awk '{print $1}'
    fi
}

# --- Check 1: pinned generator inputs match their recorded sha256 -----------
# key in UPSTREAM.lock  ->  file path relative to repo root
declare -a PINNED=(
    "sha256.ast.json:tools/generator/_scripts/ast.json"
    "sha256.schema.ts:tools/generator/_scripts/schema.ts"
    "sha256.extraDiagnosticMessages.json:tools/generator/extraDiagnosticMessages.json"
    "sha256.diagnosticMessages.json:_submodules/TypeScript/src/compiler/diagnosticMessages.json"
)

echo "==> verifying pinned generator inputs"
for entry in "${PINNED[@]}"; do
    key="${entry%%:*}"
    rel="${entry#*:}"
    path="$REPO_ROOT/$rel"
    [[ -f "$path" ]] || fail "pinned input missing: $rel (submodule not checked out?)"
    expected="$(lock_value "$key")"
    actual="$(sha256_of "$path")"
    [[ "$expected" == "$actual" ]] \
        || fail "sha256 drift on $rel: lock=$expected actual=$actual"
    echo "    ok  $rel"
done

# --- Check 2: regenerate all products and assert no working-tree drift -------
echo "==> regenerating products (npm run gen:all)"
( cd "$GEN_DIR" && npm ci --silent && npm run --silent gen:all )

echo "==> checking generated products are unchanged"
# Products live under src/**/**.g.cs plus the bundled libs. A dirty tree here
# means a committed product is stale relative to its generator/input.
if ! git -C "$REPO_ROOT" diff --exit-code -- \
        'src/**/*.g.cs' \
        'src/Ts.Compiler/Bundled/libs' >/dev/null; then
    echo "check-upstream-pin: generated products are stale — re-run the generators and commit:" >&2
    git -C "$REPO_ROOT" --no-pager diff --stat -- 'src/**/*.g.cs' 'src/Ts.Compiler/Bundled/libs' >&2
    exit 1
fi

echo "==> all checks passed"
