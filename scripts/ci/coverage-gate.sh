#!/usr/bin/env bash
# Coverage Gate Script for CI/CD
# Parses Cobertura XML coverage reports and enforces threshold
# Generates PR comment markdown with badge and per-project breakdown

set -euo pipefail

# Default values
COVERAGE_FILE=""
PER_PROJECT_DIR=""
PER_ASSEMBLY_FILE=""
THRESHOLD=80
OUTPUT_DIR="."
VERBOSE=false

# Exclude globs for --per-project / --per-assembly modes (non-shipping units:
# pure test-support projects, or non-shipping assemblies). Seeded from the
# EXCLUDE env var (whitespace-separated), then appended to by each --exclude
# flag. In --per-project mode globs match the Cobertura FILE name; in
# --per-assembly mode they match the <package> NAME.
EXCLUDE_GLOBS=()
if [[ -n "${EXCLUDE:-}" ]]; then
    # shellcheck disable=SC2206  # intentional word-splitting of the env list
    EXCLUDE_GLOBS=(${EXCLUDE})
fi

# Colors for terminal output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

usage() {
    cat << EOF
Usage: $(basename "$0") [OPTIONS]

Modes (exactly one of --coverage-file, --per-project, or --per-assembly is required):
    --coverage-file FILE    Gate a single Cobertura XML report (the merged
                            aggregate). Writes a PR-comment markdown file.
    --per-project DIR       Gate EACH *.cobertura.xml in DIR independently.
                            Fails if ANY project is below the threshold on
                            line OR branch coverage. Writes a per-project
                            pass/fail table to the PR-comment markdown.
    --per-assembly FILE     Gate EACH <package> in a single MERGED Cobertura
                            report independently — one <package name="..">
                            per shipping assembly. Fails if ANY assembly is
                            below the threshold on line OR branch coverage.
                            An empty / all-excluded package set FAILS (no
                            vacuous pass). Writes a per-assembly pass/fail
                            table to the PR-comment markdown.

Options:
    --threshold PERCENT     Minimum coverage percentage (default: 80). Applies
                            to both line and branch coverage. A file with no
                            branch-rate skips the branch check (not a failure).
    --exclude GLOB          (--per-project / --per-assembly, repeatable) Skip
                            a unit whose name matches GLOB. In --per-project
                            the glob matches the Cobertura FILE name; in
                            --per-assembly it matches the <package> NAME. Use
                            for non-shipping units. May also be supplied via
                            the EXCLUDE env var as a whitespace-separated list.
    --output-dir DIR        Directory for output files (default: current directory)
    --verbose               Enable verbose output
    -h, --help              Show this help message

Examples:
    # Aggregate gate (existing CI behavior)
    $(basename "$0") --coverage-file ./coverage/Cobertura.xml --threshold 80 --output-dir ./output

    # Per-project gate, excluding a test-support project
    $(basename "$0") --per-project ./coverage --threshold 80 --exclude '*TestSupport*'
    EXCLUDE='*TestSupport* *Fixtures*' $(basename "$0") --per-project ./coverage

    # Per-assembly gate over one merged report, excluding a non-shipping assembly
    $(basename "$0") --per-assembly ./coverage/merged.cobertura.xml --threshold 80 --exclude 'Bifrost.Scheduling.Testing'
EOF
    exit 1
}

log_info() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

log_warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

log_verbose() {
    if [[ "$VERBOSE" == "true" ]]; then
        echo -e "[DEBUG] $1"
    fi
}

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --coverage-file)
            COVERAGE_FILE="$2"
            shift 2
            ;;
        --per-project)
            PER_PROJECT_DIR="$2"
            shift 2
            ;;
        --per-assembly)
            PER_ASSEMBLY_FILE="$2"
            shift 2
            ;;
        --exclude)
            EXCLUDE_GLOBS+=("$2")
            shift 2
            ;;
        --threshold)
            THRESHOLD="$2"
            shift 2
            ;;
        --output-dir)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        --verbose)
            VERBOSE=true
            shift
            ;;
        -h|--help)
            usage
            ;;
        *)
            log_error "Unknown option: $1"
            usage
            ;;
    esac
done

# Validate required arguments: exactly one mode must be selected.
MODE_COUNT=0
[[ -n "$COVERAGE_FILE" ]] && MODE_COUNT=$((MODE_COUNT + 1))
[[ -n "$PER_PROJECT_DIR" ]] && MODE_COUNT=$((MODE_COUNT + 1))
[[ -n "$PER_ASSEMBLY_FILE" ]] && MODE_COUNT=$((MODE_COUNT + 1))

if [[ "$MODE_COUNT" -gt 1 ]]; then
    log_error "--coverage-file, --per-project, and --per-assembly are mutually exclusive"
    usage
fi

if [[ "$MODE_COUNT" -eq 0 ]]; then
    log_error "One of --coverage-file, --per-project, or --per-assembly is required"
    usage
fi

if [[ -n "$COVERAGE_FILE" && ! -f "$COVERAGE_FILE" ]]; then
    log_error "Coverage file not found: $COVERAGE_FILE"
    exit 1
fi

if [[ -n "$PER_PROJECT_DIR" && ! -d "$PER_PROJECT_DIR" ]]; then
    log_error "Per-project directory not found: $PER_PROJECT_DIR"
    exit 1
fi

if [[ -n "$PER_ASSEMBLY_FILE" && ! -f "$PER_ASSEMBLY_FILE" ]]; then
    log_error "Per-assembly merged report not found: $PER_ASSEMBLY_FILE"
    exit 1
fi

# Create output directory
mkdir -p "$OUTPUT_DIR"

log_info "Coverage Gate Analysis"
log_info "======================"
if [[ -n "$PER_PROJECT_DIR" ]]; then
    log_info "Mode: per-project"
    log_info "Coverage dir: $PER_PROJECT_DIR"
elif [[ -n "$PER_ASSEMBLY_FILE" ]]; then
    log_info "Mode: per-assembly"
    log_info "Merged report: $PER_ASSEMBLY_FILE"
else
    log_info "Mode: aggregate"
    log_info "Coverage file: $COVERAGE_FILE"
fi
log_info "Threshold: ${THRESHOLD}%"
log_info "Output directory: $OUTPUT_DIR"

# Extract coverage data from Cobertura XML
# Using xmllint or grep/sed as fallback
extract_coverage() {
    local file="$1"

    # Try to extract line-rate from coverage element
    if command -v xmllint &> /dev/null; then
        local line_rate=$(xmllint --xpath "string(//coverage/@line-rate)" "$file" 2>/dev/null || echo "")
        if [[ -n "$line_rate" ]]; then
            # Convert to percentage (line-rate is 0-1)
            echo "$line_rate" | awk '{printf "%.2f", $1 * 100}'
            return
        fi
    fi

    # Fallback: grep/sed approach
    local line_rate=$(grep -oP 'line-rate="[0-9.]*"' "$file" | head -1 | grep -oP '[0-9.]+')
    if [[ -n "$line_rate" ]]; then
        echo "$line_rate" | awk '{printf "%.2f", $1 * 100}'
        return
    fi

    log_error "Could not extract coverage data from file"
    echo "0"
}

# Extract branch coverage
extract_branch_coverage() {
    local file="$1"

    if command -v xmllint &> /dev/null; then
        local branch_rate=$(xmllint --xpath "string(//coverage/@branch-rate)" "$file" 2>/dev/null || echo "")
        if [[ -n "$branch_rate" ]]; then
            echo "$branch_rate" | awk '{printf "%.2f", $1 * 100}'
            return
        fi
    fi

    local branch_rate=$(grep -oP 'branch-rate="[0-9.]*"' "$file" | head -1 | grep -oP '[0-9.]+')
    if [[ -n "$branch_rate" ]]; then
        echo "$branch_rate" | awk '{printf "%.2f", $1 * 100}'
        return
    fi

    echo "N/A"
}

# Extract per-package coverage
extract_package_coverage() {
    local file="$1"
    local output=""

    if command -v xmllint &> /dev/null; then
        # Get all package names and their line-rates
        local packages=$(xmllint --xpath "//package/@name" "$file" 2>/dev/null | tr ' ' '\n' | grep -oP '(?<=name=")[^"]+' || echo "")

        for pkg in $packages; do
            local pkg_rate=$(xmllint --xpath "string(//package[@name='$pkg']/@line-rate)" "$file" 2>/dev/null || echo "0")
            local pkg_coverage=$(echo "$pkg_rate" | awk '{printf "%.1f", $1 * 100}')
            output+="| $pkg | ${pkg_coverage}% |\n"
        done
    fi

    if [[ -z "$output" ]]; then
        # Fallback: try to parse with grep/sed
        while IFS= read -r line; do
            if [[ "$line" =~ name=\"([^\"]+)\".*line-rate=\"([0-9.]+)\" ]]; then
                local pkg_name="${BASH_REMATCH[1]}"
                local pkg_rate="${BASH_REMATCH[2]}"
                local pkg_coverage=$(echo "$pkg_rate" | awk '{printf "%.1f", $1 * 100}')
                output+="| $pkg_name | ${pkg_coverage}% |\n"
            fi
        done < <(grep -oP '<package[^>]+>' "$file")
    fi

    echo -e "$output"
}

# Extract per-package (assembly) rows from a single MERGED Cobertura report.
# Emits one TAB-separated record per <package>:  name<TAB>line%<TAB>branch%
# where line%/branch% are 0-100 with two decimals (branch% is "N/A" if the
# package has no branch-rate attribute). Works on the xmllint path and the
# grep/sed fallback (xmllint absent). Mirrors extract_package_coverage's
# extraction strategy but carries branch-rate too and stays machine-readable.
extract_assembly_rows() {
    local file="$1"
    local emitted=0

    if command -v xmllint &> /dev/null; then
        local packages
        packages=$(xmllint --xpath "//package/@name" "$file" 2>/dev/null \
            | tr ' ' '\n' | grep -oP '(?<=name=")[^"]+' || echo "")

        local pkg
        for pkg in $packages; do
            local line_rate branch_rate line_pct branch_pct
            line_rate=$(xmllint --xpath "string(//package[@name='$pkg']/@line-rate)" "$file" 2>/dev/null || echo "")
            branch_rate=$(xmllint --xpath "string(//package[@name='$pkg']/@branch-rate)" "$file" 2>/dev/null || echo "")

            if [[ -n "$line_rate" ]]; then
                line_pct=$(echo "$line_rate" | awk '{printf "%.2f", $1 * 100}')
            else
                line_pct="0.00"
            fi
            if [[ -n "$branch_rate" ]]; then
                branch_pct=$(echo "$branch_rate" | awk '{printf "%.2f", $1 * 100}')
            else
                branch_pct="N/A"
            fi

            printf '%s\t%s\t%s\n' "$pkg" "$line_pct" "$branch_pct"
            emitted=1
        done
    fi

    if [[ "$emitted" -eq 0 ]]; then
        # Fallback: parse each <package ...> open tag with grep/sed. Real
        # ReportGenerator / coverlet output orders name before the rate attrs.
        while IFS= read -r tag; do
            [[ "$tag" =~ name=\"([^\"]+)\" ]] || continue
            local pkg_name="${BASH_REMATCH[1]}"

            local line_pct="0.00" branch_pct="N/A"
            if [[ "$tag" =~ line-rate=\"([0-9.]+)\" ]]; then
                line_pct=$(echo "${BASH_REMATCH[1]}" | awk '{printf "%.2f", $1 * 100}')
            fi
            if [[ "$tag" =~ branch-rate=\"([0-9.]+)\" ]]; then
                branch_pct=$(echo "${BASH_REMATCH[1]}" | awk '{printf "%.2f", $1 * 100}')
            fi

            printf '%s\t%s\t%s\n' "$pkg_name" "$line_pct" "$branch_pct"
        done < <(grep -oP '<package[^>]+>' "$file")
    fi
}

# Decide pass/fail for a single (line%, branch%) pair against THRESHOLD.
# Gates BOTH line and branch coverage. A branch value of "N/A" (no branch-rate
# in the report) skips the branch check so single-file callers with line-only
# reports stay green. Echoes "pass" or "fail"; returns 0/1 accordingly.
check_thresholds() {
    local line_pct="$1"
    local branch_pct="$2"

    local line_int
    line_int=$(echo "$line_pct" | awk '{print int($1)}')
    if [[ "$line_int" -lt "$THRESHOLD" ]]; then
        echo "fail"
        return 1
    fi

    if [[ -n "$branch_pct" && "$branch_pct" != "N/A" ]]; then
        local branch_int
        branch_int=$(echo "$branch_pct" | awk '{print int($1)}')
        if [[ "$branch_int" -lt "$THRESHOLD" ]]; then
            echo "fail"
            return 1
        fi
    fi

    echo "pass"
    return 0
}

# True if the given filename matches any configured exclude glob.
is_excluded() {
    local name="$1"
    local glob
    for glob in "${EXCLUDE_GLOBS[@]+"${EXCLUDE_GLOBS[@]}"}"; do
        # shellcheck disable=SC2053  # RHS is an intentional glob pattern
        if [[ "$name" == $glob ]]; then
            return 0
        fi
    done
    return 1
}

# ---------------------------------------------------------------------------
# Per-assembly mode: gate each <package> in a single MERGED Cobertura report.
# Each <package name="AssemblyName" line-rate=".." branch-rate=".."> is one
# shipping assembly. Fails if ANY (non-excluded) assembly is below threshold
# on line OR branch. An empty / fully-excluded set FAILS (no vacuous pass).
# ---------------------------------------------------------------------------
if [[ -n "$PER_ASSEMBLY_FILE" ]]; then
    PR_COMMENT_FILE="$OUTPUT_DIR/pr-comment.md"
    OVERALL_STATUS="passing"
    ROWS=""
    CONSIDERED=0

    while IFS=$'\t' read -r asm_name asm_line asm_branch; do
        [[ -z "$asm_name" ]] && continue

        if is_excluded "$asm_name"; then
            log_info "Excluding ${asm_name} (matched exclude glob)"
            ROWS+="| ${asm_name} | - | - | :fast_forward: Excluded |\n"
            continue
        fi

        CONSIDERED=$((CONSIDERED + 1))
        # '|| true' keeps `set -e` from aborting on a failing assembly — we
        # branch on the echoed "pass"/"fail" string, not the exit status.
        asm_result=$(check_thresholds "$asm_line" "$asm_branch") || true

        if [[ "$asm_result" == "pass" ]]; then
            log_info "PASS ${asm_name}: line ${asm_line}% / branch ${asm_branch}%"
            ROWS+="| ${asm_name} | ${asm_line}% | ${asm_branch}% | :white_check_mark: Pass |\n"
        else
            log_error "FAIL ${asm_name}: line ${asm_line}% / branch ${asm_branch}% (threshold ${THRESHOLD}%)"
            ROWS+="| ${asm_name} | ${asm_line}% | ${asm_branch}% | :x: Fail |\n"
            OVERALL_STATUS="failing"
        fi
    done < <(extract_assembly_rows "$PER_ASSEMBLY_FILE")

    if [[ "$CONSIDERED" -eq 0 ]]; then
        log_error "No (non-excluded) <package> assemblies found in: $PER_ASSEMBLY_FILE"
        OVERALL_STATUS="failing"
    fi

    # Write the per-assembly PR comment table.
    {
        echo "## :bar_chart: Per-Assembly Coverage Report"
        echo
        echo "| Assembly | Line | Branch | Status |"
        echo "|----------|------|--------|--------|"
        echo -en "$ROWS"
        echo
        echo "---"
        echo "<sub>Generated by coverage-gate.sh | Per-assembly gate | Threshold: ${THRESHOLD}% (line + branch)</sub>"
    } > "$PR_COMMENT_FILE"

    log_info "PR comment written to: $PR_COMMENT_FILE"

    if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
        echo "threshold=$THRESHOLD" >> "$GITHUB_OUTPUT"
        echo "status=$OVERALL_STATUS" >> "$GITHUB_OUTPUT"
    fi

    if [[ "$OVERALL_STATUS" == "failing" ]]; then
        log_error "Per-assembly coverage gate FAILED"
        exit 1
    fi

    log_info "Per-assembly coverage gate PASSED"
    exit 0
fi

# ---------------------------------------------------------------------------
# Per-project mode: gate each *.cobertura.xml in the directory independently.
# ---------------------------------------------------------------------------
if [[ -n "$PER_PROJECT_DIR" ]]; then
    PR_COMMENT_FILE="$OUTPUT_DIR/pr-comment.md"
    OVERALL_STATUS="passing"
    ROWS=""
    CONSIDERED=0

    shopt -s nullglob
    for cov_file in "$PER_PROJECT_DIR"/*.cobertura.xml; do
        base="$(basename "$cov_file")"

        if is_excluded "$base"; then
            log_info "Excluding ${base} (matched exclude glob)"
            ROWS+="| ${base} | - | - | :fast_forward: Excluded |\n"
            continue
        fi

        CONSIDERED=$((CONSIDERED + 1))
        proj_line=$(extract_coverage "$cov_file")
        proj_branch=$(extract_branch_coverage "$cov_file")
        # '|| true' keeps `set -e` from aborting on a failing project — we
        # branch on the echoed "pass"/"fail" string, not the exit status.
        proj_result=$(check_thresholds "$proj_line" "$proj_branch") || true

        if [[ "$proj_result" == "pass" ]]; then
            log_info "PASS ${base}: line ${proj_line}% / branch ${proj_branch}%"
            ROWS+="| ${base} | ${proj_line}% | ${proj_branch}% | :white_check_mark: Pass |\n"
        else
            log_error "FAIL ${base}: line ${proj_line}% / branch ${proj_branch}% (threshold ${THRESHOLD}%)"
            ROWS+="| ${base} | ${proj_line}% | ${proj_branch}% | :x: Fail |\n"
            OVERALL_STATUS="failing"
        fi
    done
    shopt -u nullglob

    if [[ "$CONSIDERED" -eq 0 ]]; then
        log_error "No (non-excluded) *.cobertura.xml files found in: $PER_PROJECT_DIR"
        OVERALL_STATUS="failing"
    fi

    # Write the per-project PR comment table.
    {
        echo "## :bar_chart: Per-Project Coverage Report"
        echo
        echo "| Project | Line | Branch | Status |"
        echo "|---------|------|--------|--------|"
        echo -en "$ROWS"
        echo
        echo "---"
        echo "<sub>Generated by coverage-gate.sh | Per-project gate | Threshold: ${THRESHOLD}% (line + branch)</sub>"
    } > "$PR_COMMENT_FILE"

    log_info "PR comment written to: $PR_COMMENT_FILE"

    if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
        echo "threshold=$THRESHOLD" >> "$GITHUB_OUTPUT"
        echo "status=$OVERALL_STATUS" >> "$GITHUB_OUTPUT"
    fi

    if [[ "$OVERALL_STATUS" == "failing" ]]; then
        log_error "Per-project coverage gate FAILED"
        exit 1
    fi

    log_info "Per-project coverage gate PASSED"
    exit 0
fi

# ---------------------------------------------------------------------------
# Single-file (aggregate) mode — backward compatible.
# ---------------------------------------------------------------------------

# Get coverage values
COVERAGE=$(extract_coverage "$COVERAGE_FILE")
BRANCH_COVERAGE=$(extract_branch_coverage "$COVERAGE_FILE")

log_info "Line Coverage: ${COVERAGE}%"
log_info "Branch Coverage: ${BRANCH_COVERAGE}%"
log_info "Threshold: ${THRESHOLD}%"

# Determine pass/fail — gates BOTH line and branch coverage.
# Branch coverage of "N/A" (line-only report) skips the branch check.
# '|| true' stops `set -e` from aborting before the PR comment is written;
# we still gate on the echoed result and exit 1 at the end.
GATE_RESULT=$(check_thresholds "$COVERAGE" "$BRANCH_COVERAGE") || true
if [[ "$GATE_RESULT" == "pass" ]]; then
    STATUS="passing"
    STATUS_EMOJI="white_check_mark"
    STATUS_COLOR="brightgreen"
    log_info "Coverage gate PASSED"
else
    STATUS="failing"
    STATUS_EMOJI="x"
    STATUS_COLOR="red"
    log_error "Coverage gate FAILED"
fi

# Generate badge URL (shields.io)
BADGE_URL="https://img.shields.io/badge/coverage-${COVERAGE}%25-${STATUS_COLOR}"

# Branch-coverage row presentation: gated when present, "skipped" when N/A.
if [[ -z "$BRANCH_COVERAGE" || "$BRANCH_COVERAGE" == "N/A" ]]; then
    BRANCH_THRESHOLD_LABEL="-"
    BRANCH_STATUS_CELL=":heavy_minus_sign: N/A"
else
    BRANCH_THRESHOLD_LABEL="${THRESHOLD}%"
    BRANCH_INT=$(echo "$BRANCH_COVERAGE" | awk '{print int($1)}')
    if [[ "$BRANCH_INT" -ge "$THRESHOLD" ]]; then
        BRANCH_STATUS_CELL=":white_check_mark: Passing"
    else
        BRANCH_STATUS_CELL=":x: Failing"
    fi
fi

# Get package breakdown
PACKAGE_BREAKDOWN=$(extract_package_coverage "$COVERAGE_FILE")

# Generate PR comment markdown
PR_COMMENT_FILE="$OUTPUT_DIR/pr-comment.md"

cat > "$PR_COMMENT_FILE" << EOF
## :bar_chart: Coverage Report

![Coverage](${BADGE_URL})

| Metric | Value | Threshold | Status |
|--------|-------|-----------|--------|
| Line Coverage | **${COVERAGE}%** | ${THRESHOLD}% | :${STATUS_EMOJI}: ${STATUS^} |
| Branch Coverage | ${BRANCH_COVERAGE}% | ${BRANCH_THRESHOLD_LABEL} | ${BRANCH_STATUS_CELL} |

EOF

# Add package breakdown if available
if [[ -n "$PACKAGE_BREAKDOWN" ]]; then
    cat >> "$PR_COMMENT_FILE" << EOF
### Per-Package Coverage

| Package | Coverage |
|---------|----------|
${PACKAGE_BREAKDOWN}
EOF
fi

# Add footer
cat >> "$PR_COMMENT_FILE" << EOF

---
<sub>Generated by coverage-gate.sh | Threshold: ${THRESHOLD}%</sub>
EOF

log_info "PR comment written to: $PR_COMMENT_FILE"

# Output for GitHub Actions
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    echo "coverage=$COVERAGE" >> "$GITHUB_OUTPUT"
    echo "branch_coverage=$BRANCH_COVERAGE" >> "$GITHUB_OUTPUT"
    echo "threshold=$THRESHOLD" >> "$GITHUB_OUTPUT"
    echo "status=$STATUS" >> "$GITHUB_OUTPUT"
fi

# Exit with appropriate code
if [[ "$STATUS" == "failing" ]]; then
    exit 1
fi

exit 0
