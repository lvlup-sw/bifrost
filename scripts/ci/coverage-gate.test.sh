#!/usr/bin/env bash
# Test harness for coverage-gate.sh
#
# Plain-bash, dependency-light. Runs the production script against fixed
# Cobertura fixtures and asserts on the EXIT CODE (the gate's contract).
#
# Run:  bash scripts/ci/coverage-gate.test.sh
#
# Each test invokes coverage-gate.sh in a throwaway tmp output dir so the
# generated pr-comment.md never pollutes the repo. Output is captured and
# only echoed on failure to keep the log readable.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATE="$SCRIPT_DIR/coverage-gate.sh"
DATA="$SCRIPT_DIR/testdata"

PASS=0
FAIL=0

# run_gate <expected-exit-code> <description> -- <gate args...>
# Asserts coverage-gate.sh exits with the expected code.
run_gate() {
    local expected="$1"; shift
    local desc="$1"; shift
    # consume the literal "--" separator
    [[ "$1" == "--" ]] && shift

    local tmp_out
    tmp_out="$(mktemp -d)"
    local log
    log="$(bash "$GATE" --output-dir "$tmp_out" "$@" 2>&1)"
    local actual=$?
    rm -rf "$tmp_out"

    if [[ "$actual" -eq "$expected" ]]; then
        echo "PASS: $desc (exit $actual)"
        PASS=$((PASS + 1))
    else
        echo "FAIL: $desc — expected exit $expected, got $actual"
        echo "----- captured output -----"
        echo "$log"
        echo "---------------------------"
        FAIL=$((FAIL + 1))
    fi
}

echo "=== coverage-gate.sh test harness ==="
echo

# ---------------------------------------------------------------------------
# task-15 — branch-coverage gating
# ---------------------------------------------------------------------------

# RED: line 85 passes, but branch 70 is below the 80 threshold → must FAIL.
run_gate 1 "task-15: branch 0.70 < threshold 80 fails the gate" -- \
    --coverage-file "$DATA/line85-branch70.cobertura.xml" --threshold 80

# GREEN: both line and branch at 0.85 → passes.
run_gate 0 "task-15: line 0.85 + branch 0.85 >= threshold 80 passes" -- \
    --coverage-file "$DATA/line85-branch85.cobertura.xml" --threshold 80

# Backward-compat: branch-rate absent (N/A) must NOT crash; line passes → exit 0.
NA_FIXTURE="$(mktemp --suffix=.cobertura.xml)"
cat > "$NA_FIXTURE" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="0.85" version="1.9">
  <packages>
    <package name="NoBranch.Package" line-rate="0.85">
      <classes />
    </package>
  </packages>
</coverage>
XML
run_gate 0 "task-15: missing branch-rate skips branch gate (backward compat)" -- \
    --coverage-file "$NA_FIXTURE" --threshold 80
rm -f "$NA_FIXTURE"

# ---------------------------------------------------------------------------
# task-16 — per-project gating mode
# ---------------------------------------------------------------------------

# RED: dir has A (0.90/0.90) and B (0.60/0.55); B is below 80 → must FAIL.
run_gate 1 "task-16: --per-project fails when any project (B) is below threshold" -- \
    --per-project "$DATA/perproject" --threshold 80

# GREEN: all projects >= 80 → passes.
run_gate 0 "task-16: --per-project passes when all projects meet threshold" -- \
    --per-project "$DATA/perproject-pass" --threshold 80 \
    --exclude '*TestSupport*'

# The pass dir contains a low-coverage TestSupport file; WITHOUT excluding it
# the gate must FAIL — proves the exclude flag is what makes it pass above.
run_gate 1 "task-16: --per-project without exclude catches the low TestSupport project" -- \
    --per-project "$DATA/perproject-pass" --threshold 80

echo
echo "=== $PASS passed, $FAIL failed ==="
[[ "$FAIL" -eq 0 ]]
