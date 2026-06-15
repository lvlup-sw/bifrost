#!/usr/bin/env bash
# Coverage Gate Script for CI/CD
# Parses Cobertura XML coverage reports and enforces threshold
# Generates PR comment markdown with badge and per-project breakdown

set -euo pipefail

# Default values
COVERAGE_FILE=""
THRESHOLD=80
OUTPUT_DIR="."
VERBOSE=false

# Colors for terminal output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

usage() {
    cat << EOF
Usage: $(basename "$0") [OPTIONS]

Options:
    --coverage-file FILE    Path to Cobertura XML coverage report (required)
    --threshold PERCENT     Minimum coverage percentage (default: 80)
    --output-dir DIR        Directory for output files (default: current directory)
    --verbose               Enable verbose output
    -h, --help              Show this help message

Example:
    $(basename "$0") --coverage-file ./coverage/Cobertura.xml --threshold 80 --output-dir ./output
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

# Validate required arguments
if [[ -z "$COVERAGE_FILE" ]]; then
    log_error "Coverage file is required"
    usage
fi

if [[ ! -f "$COVERAGE_FILE" ]]; then
    log_error "Coverage file not found: $COVERAGE_FILE"
    exit 1
fi

# Create output directory
mkdir -p "$OUTPUT_DIR"

log_info "Coverage Gate Analysis"
log_info "======================"
log_info "Coverage file: $COVERAGE_FILE"
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

# Get coverage values
COVERAGE=$(extract_coverage "$COVERAGE_FILE")
BRANCH_COVERAGE=$(extract_branch_coverage "$COVERAGE_FILE")

log_info "Line Coverage: ${COVERAGE}%"
log_info "Branch Coverage: ${BRANCH_COVERAGE}%"
log_info "Threshold: ${THRESHOLD}%"

# Determine pass/fail
COVERAGE_INT=$(echo "$COVERAGE" | awk '{print int($1)}')
if [[ $COVERAGE_INT -ge $THRESHOLD ]]; then
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
| Branch Coverage | ${BRANCH_COVERAGE}% | - | - |

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
