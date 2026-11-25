#!/usr/bin/env bash
# Publishes one AOT smoke sample with Native AOT for linux-x64 and runs the native binary.
#
#   scripts/ci/aot.sh scheduling     samples/Bifrost.Scheduling.AotSmoke
#   scripts/ci/aot.sh concurrency    samples/Bifrost.Concurrency.AotSmoke
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

case "${1:-}" in
  scheduling) project=Bifrost.Scheduling.AotSmoke ;;
  concurrency) project=Bifrost.Concurrency.AotSmoke ;;
  *)
    echo "Usage: scripts/ci/aot.sh scheduling|concurrency" >&2
    exit 2
    ;;
esac

publish="$(mktemp -d)"
trap 'rm -rf "$publish"' EXIT
dotnet publish "samples/$project/$project.csproj" -c Release -r linux-x64 -o "$publish"
"$publish/$project"
