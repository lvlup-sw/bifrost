#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
policy=(dotnet tool run lvlup-policy --)
if [[ -n "${LVLUP_POLICY_TOOL_DLL:-}" ]]; then
  policy=(dotnet "$LVLUP_POLICY_TOOL_DLL")
fi
mode="${1:-local}"
args=()
if [[ "$mode" == ci ]]; then
  case "${GITHUB_EVENT_NAME:?GitHub event name is required}" in
    pull_request)
      mode="pr"
      base="$(python3 -c 'import json,os; print(json.load(open(os.environ["GITHUB_EVENT_PATH"]))["pull_request"]["base"]["sha"])')"
      args+=(--base "$base")
      ;;
    push)
      base="$(python3 -c 'import json,os; print(json.load(open(os.environ["GITHUB_EVENT_PATH"]))["before"])')"
      if [[ "$base" =~ ^0{40}$ ]]; then
        mode=genesis
      else
        mode=push
        args+=(--base "$base")
      fi
      ;;
    workflow_dispatch|schedule) mode=main ;;
    *) echo "Unsupported GitHub event: $GITHUB_EVENT_NAME" >&2; exit 2 ;;
  esac
fi
"${policy[@]}" check --root "$ROOT" --mode "$mode" --subject "$(git rev-parse HEAD)" \
  --policy verification/policy/repository.json \
  --baseline verification/policy/comment-baseline.json \
  --report "${LVLUP_POLICY_REPORT:-artifacts/conventions.json}" ${args[@]+"${args[@]}"}
