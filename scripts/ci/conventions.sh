#!/usr/bin/env bash
# Runs scripts/check-conventions.sh in PR mode on the tested merge. The base is the merge's
# first parent: the commit the change is merged onto.
#
#   scripts/ci/conventions.sh
#
# The report goes to artifacts/conventions.json.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

if ! base="$(git rev-parse --verify --quiet 'HEAD^1')"; then
  echo "The conventions check needs the tested merge's first parent. Use a checkout that is not shallow." >&2
  exit 1
fi
dotnet tool restore
mkdir -p artifacts
scripts/check-conventions.sh pr "$base"
