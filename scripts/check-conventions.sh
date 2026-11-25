#!/usr/bin/env bash
# Runs the lvlup-policy comment check (comments/no-function-body) over every tracked C# file.
#
#   scripts/check-conventions.sh              local mode: the working tree, untracked files included
#   scripts/check-conventions.sh pr <base>    PR mode against <base>, as the Buildkite conventions job
#                                             runs it (base = the commit the change is merged onto)
#   scripts/check-conventions.sh main         main mode against HEAD's first parent
#
# LVLUP_POLICY_TOOL_DLL runs a locally built tool; LVLUP_POLICY_REPORT moves the JSON report.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
policy=(dotnet tool run lvlup-policy --)
if [[ -n "${LVLUP_POLICY_TOOL_DLL:-}" ]]; then
  policy=(dotnet "$LVLUP_POLICY_TOOL_DLL")
fi
mode="${1:-local}"
args=()
if [[ "$mode" == pr ]]; then
  if [[ $# -ne 2 ]] || ! base="$(git rev-parse --verify --quiet "$2^{commit}")"; then
    echo "Usage: scripts/check-conventions.sh pr <base commit>" >&2
    exit 2
  fi
  args+=(--base "$base")
fi
"${policy[@]}" check --root "$ROOT" --mode "$mode" --subject "$(git rev-parse HEAD)" \
  --policy verification/policy/repository.json \
  --baseline verification/policy/comment-baseline.json \
  --report "${LVLUP_POLICY_REPORT:-artifacts/conventions.json}" ${args[@]+"${args[@]}"}
