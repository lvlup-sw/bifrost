#!/usr/bin/env bash
# Runs every test project except the Stress category with coverage, then enforces the
# 80% line and branch gate on each assembly in the merged report (scripts/ci/results.py).
#
#   scripts/ci/test.sh
#
# Results go to artifacts/ci/coverage. On a tested merge whose change stays inside the
# library sources, only the test projects and their references are built. Any other change
# (samples, benchmarks, build files, CI files) builds the whole solution.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

solution=src/Bifrost.sln
roster=(
  src/Bifrost.Tests.Concurrency/Bifrost.Tests.Concurrency.csproj
  src/Bifrost.Tests.Scheduling/Bifrost.Tests.Scheduling.csproj
  src/Bifrost.Tests/Bifrost.Tests.csproj
)
out="$PWD/artifacts/ci/coverage"
reportgenerator=/tools/reportgenerator
[[ -x "$reportgenerator" ]] || reportgenerator=reportgenerator

discovered="$(printf '%s\n' src/Bifrost.Tests*/*.csproj | LC_ALL=C sort)"
if [[ "$discovered" != "$(printf '%s\n' "${roster[@]}" | LC_ALL=C sort)" ]]; then
  printf 'Test projects differ from the roster in scripts/ci/test.sh:\n%s\n' "$discovered" >&2
  exit 1
fi

# Succeeds unless HEAD^1..HEAD changes only library sources. Without a first parent, the
# change is unknown, so the whole solution builds.
whole_solution() {
  local changed
  if ! changed="$(git diff --name-only 'HEAD^1' HEAD 2>/dev/null)" || [[ -z "$changed" ]]; then
    return 0
  fi
  grep -q -E '^(samples/|src/Bifrost\.Benchmarks/|global\.json$|nuget\.config$|\.editorconfig$|\.buildkite/|scripts/ci/)|\.(sln|slnx|props|targets)$' <<<"$changed"
}

scope="$solution"
if ! whole_solution; then
  scope_dir="$(mktemp -d)"
  trap 'rm -rf "$scope_dir"' EXIT
  scope="$scope_dir/coverage.slnf"
  projects="$(printf '","%s' "${roster[@]#src/}")"
  printf '{"solution":{"path":"%s","projects":["%s"]}}\n' "$PWD/$solution" "${projects:3}" >"$scope"
  echo "Building the test roster only: the change stays inside the library sources."
fi
dotnet restore "$scope"
dotnet build "$scope" -c Release --no-restore --disable-build-servers

rm -rf "$out"
mkdir -p "$out"
export BIFROST_INVARIANT_SEED_MULTIPLIER=1
export BIFROST_REPLAY_OUTPUT="$out/replays" BIFROST_REGISTRY_ARTIFACTS="$out/registry"
for project in "${roster[@]}"; do
  results="$out/$(basename "$project" .csproj)"
  mkdir "$results"
  dotnet run --project "$project" -c Release --no-build --no-restore -- \
    --treenode-filter '/*/*/*/*[Category!=Stress]' \
    --coverage --coverage-settings "$PWD/scripts/ci/coverage-settings.xml" \
    --coverage-output-format cobertura --coverage-output "$results/coverage.cobertura.xml" \
    --report-trx --report-trx-filename tests.trx --results-directory "$results"
done

"$reportgenerator" "-reports:$out/*/coverage.cobertura.xml" "-targetdir:$out" \
  -reporttypes:Cobertura -verbosity:Warning
python3 scripts/ci/results.py coverage "$out"
