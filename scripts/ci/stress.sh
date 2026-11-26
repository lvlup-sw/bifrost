#!/usr/bin/env bash
# Runs the Stress category of Bifrost.Tests.Concurrency, which scripts/ci/test.sh leaves out.
#
#   scripts/ci/stress.sh
#
# Results go to artifacts/ci/stress. At least one stress test must run and every one must pass.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

out="$PWD/artifacts/ci/stress"
rm -rf "$out"
mkdir -p "$out"
dotnet run --project src/Bifrost.Tests.Concurrency -c Release -- \
  --treenode-filter '/*/*/*/*[Category=Stress]' --minimum-expected-tests 1 \
  --report-trx --report-trx-filename tests.trx --results-directory "$out"
python3 scripts/ci/results.py trx "$out/tests.trx"
