#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C

readonly CLEAN_REFERENCE_FILES=49321
readonly CLEAN_TOTAL_FILES=49642
readonly CLEAN_TOTAL_BYTES=147111511

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
readonly REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd -P)"
readonly DEST="$REPO_ROOT/testdata"
readonly LOCK_FILE="$REPO_ROOT/UPSTREAM.lock"
readonly TSGO="${TSGO_ROOT:-/Users/token/Desktop/code/typescript-go-main}"

usage() {
    echo "usage: tools/sync-testdata.sh [--verify]" >&2
    exit 2
}

fail() {
    echo "sync-testdata: $*" >&2
    exit 1
}

lock_value() {
    local key="$1"
    local value

    if ! value="$(
        awk -v key="$key" '
            $1 == key && $2 == "=" {
                count++
                value = $3
            }
            END {
                if (count != 1 || value !~ /^[0-9]+$/) {
                    exit 1
                }
                print value
            }
        ' "$LOCK_FILE"
    )"; then
        fail "expected exactly one numeric '$key' entry in $LOCK_FILE"
    fi

    printf '%s\n' "$value"
}

assert_equal() {
    local actual="$1"
    local expected="$2"
    local label="$3"

    if [[ "$actual" != "$expected" ]]; then
        fail "$label: expected $expected, got $actual"
    fi
}

count_files() {
    find "$@" -type f -exec sh -c 'printf "%s\n" "$#"' sh {} + \
        | awk '{ total += $1 } END { print total + 0 }'
}

sum_file_bytes() {
    # Redirect each file into wc so its path is never printed or reparsed;
    # this remains safe even if an upstream filename contains whitespace.
    find "$@" -type f -exec sh -c 'for file do wc -c < "$file"; done' sh {} + \
        | awk '{ total += $1 } END { printf "%.0f\n", total }'
}

mode="sync"
case "$#" in
    0)
        ;;
    1)
        [[ "$1" == "--verify" ]] || usage
        mode="verify"
        ;;
    *)
        usage
        ;;
esac

command -v rsync >/dev/null 2>&1 || fail "rsync is required"
[[ -f "$LOCK_FILE" ]] || fail "missing $LOCK_FILE"
[[ -d "$TSGO/testdata" ]] || fail "missing source directory: $TSGO/testdata"

readonly SOURCE="$(cd "$TSGO/testdata" && pwd -P)"
[[ "$SOURCE" != "$DEST" ]] || fail "source and destination must be different directories"

expected_reference="$(lock_value testdata.files.reference)"
expected_total="$(lock_value testdata.files.total)"
expected_bytes="$(lock_value testdata.bytes)"

# The clean pinned snapshot values are deliberate constants, not values to be
# silently updated when a contaminated source or lock file is encountered.
assert_equal "$expected_reference" "$CLEAN_REFERENCE_FILES" "UPSTREAM.lock testdata.files.reference"
assert_equal "$expected_total" "$CLEAN_TOTAL_FILES" "UPSTREAM.lock testdata.files.total"
assert_equal "$expected_bytes" "$CLEAN_TOTAL_BYTES" "UPSTREAM.lock testdata.bytes"

rsync_args=(
    --recursive
    --links
    --exclude 'baselines/local/'
    --exclude '.DS_Store'
)

if [[ "$mode" == "sync" ]]; then
    mkdir -p "$DEST"
    rsync --archive --delete \
        --exclude 'baselines/local/' \
        --exclude '.DS_Store' \
        "$SOURCE/" "$DEST/"

    # Excluded files are not removed by --delete. Preserve baselines/local/
    # while removing Finder contamination that may predate this sync.
    find "$DEST" \( -type f -o -type l \) -name '.DS_Store' -delete
else
    [[ -d "$DEST" ]] || fail "missing destination directory: $DEST"

    # A leading '.' in rsync's itemized form means metadata-only drift. Git
    # checkouts need not share mtimes, so verify only structural or data
    # changes (creation, deletion, checksum/size, or link-target changes).
    differences="$(
        rsync "${rsync_args[@]}" \
            --checksum \
            --dry-run \
            --delete \
            --itemize-changes \
            --out-format='%i %n%L' \
            "$SOURCE/" "$DEST/" \
            | awk 'substr($0, 1, 1) != "."'
    )"
    if [[ -n "$differences" ]]; then
        printf '%s\n' "$differences" >&2
        fail "testdata mirror differs from $SOURCE"
    fi
fi

contaminant="$(find "$DEST" -name '.DS_Store' -print -quit)"
[[ -z "$contaminant" ]] || fail "Finder metadata is not allowed: $contaminant"

total="$(
    count_files "$DEST" \
        ! -path "$DEST/baselines/local/*" \
        ! -name '.DS_Store'
)"
reference="$(
    count_files "$DEST/baselines/reference" \
        ! -name '.DS_Store'
)"
bytes="$(
    sum_file_bytes "$DEST" \
        ! -path "$DEST/baselines/local/*" \
        ! -name '.DS_Store'
)"

assert_equal "$reference" "$expected_reference" "testdata.files.reference"
assert_equal "$total" "$expected_total" "testdata.files.total"
assert_equal "$bytes" "$expected_bytes" "testdata.bytes"

printf 'testdata.files.total = %s\n' "$total"
printf 'testdata.files.reference = %s\n' "$reference"
printf 'testdata.bytes = %s\n' "$bytes"
