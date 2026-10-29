#!/usr/bin/env bash
# Build and verify benchmark inputs. Only the BDN Dry smoke executes timings.
set -euo pipefail

if [[ $# != 1 || $1 == --help ]]; then
  echo "Usage: $0 NEW_OUTPUT_DIRECTORY"
  echo "Prepares binaries, provenance, correctness checks, and a Dry execution smoke."
  echo "Does not run the performance campaign or provision a benchmark host."
  [[ ${1:-} == --help ]] && exit 0
  exit 2
fi

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$repo_root"
control_sha=6800ae01b59bacff024821f69f62bbce4b98fe7d
for command in dotnet git python3 tar diff sha256sum; do
  command -v "$command" >/dev/null || { echo "Missing prerequisite: $command" >&2; exit 1; }
done
git cat-file -e "$control_sha^{commit}" 2>/dev/null || {
  echo "Missing historical control. Fetch it first: git fetch origin $control_sha" >&2
  exit 1
}
if [[ -e $1 ]]; then
  echo "Output directory must be new: $1" >&2
  exit 2
fi
mkdir -p -- "$1"
run_root=$(cd -- "$1" && pwd)
control_root="$run_root/controls"
bench_project="$repo_root/src/Bifrost.Benchmarks/Bifrost.Benchmarks.csproj"
bench_dll="$repo_root/src/Bifrost.Benchmarks/bin/Release/net10.0/Bifrost.Benchmarks.dll"
mkdir -p "$run_root/logs" "$control_root/original"

run() {
  local name=$1
  shift
  echo "Preparing: $name"
  if ! "$@" > "$run_root/logs/$name.log" 2>&1; then
    tail -60 "$run_root/logs/$name.log" >&2
    echo "Failed: $name. All logs retained in $run_root" >&2
    exit 1
  fi
}

git rev-parse HEAD > "$run_root/commit.txt"
printf '%s\n' "$control_sha" > "$run_root/control-commit.txt"
git status --short > "$run_root/worktree-status.txt"
git diff --binary HEAD > "$run_root/worktree.patch"
git ls-files --others --exclude-standard -z | tar --null -T - -czf "$run_root/added-inputs.tar.gz"
sha256sum src/Bifrost.Concurrency/*.cs tools/queue-validation/{Program.cs,Validation.csproj} \
  tools/queue-profile/{Program.cs,Profile.csproj} \
  > "$run_root/source-hashes.txt"
dotnet --info > "$run_root/dotnet-info.txt"
dotnet --list-runtimes > "$run_root/runtimes.txt"
uname -a > "$run_root/uname.txt"
env | sort | sed -n '/^DOTNET_/p; /^COMPlus_/p' > "$run_root/runtime-env.txt"
if command -v lscpu >/dev/null; then lscpu > "$run_root/lscpu.txt"; fi
if command -v taskset >/dev/null; then taskset -pc $$ > "$run_root/affinity.txt"; fi
python3 - "$run_root/host-policy.txt" <<'PY'
import glob, os, pathlib, sys
paths = ["/proc/self/cgroup", "/sys/fs/cgroup/cpu.max", "/sys/fs/cgroup/cpuset.cpus.effective",
         "/sys/fs/cgroup/cpu/cpu.cfs_quota_us", "/sys/fs/cgroup/cpu/cpu.cfs_period_us"]
paths += glob.glob("/sys/devices/system/cpu/cpu[0-9]*/cpufreq/scaling_governor")
with open(sys.argv[1], "w") as output:
    output.write(f"os.cpu_count={os.cpu_count()}\n")
    for path in paths:
        try: output.write(f"{path}: {pathlib.Path(path).read_text().strip()}\n")
        except OSError as error: output.write(f"{path}: unavailable ({error})\n")
PY

run restore dotnet restore src/Bifrost.sln
run build dotnet build src/Bifrost.sln -c Release --no-restore
run packages dotnet list src/Bifrost.sln package --include-transitive --format json
for project in Bifrost.Tests Bifrost.Tests.Scheduling Bifrost.Tests.Concurrency; do
  run "$project" dotnet run --project "src/$project/$project.csproj" -c Release --no-build -- \
    --treenode-filter '/*/*/*/*[Category!=Stress]'
done
run stress dotnet run --project src/Bifrost.Tests.Concurrency -c Release --no-build -- \
  --treenode-filter '/*/*/*/*[Category=Stress]'

git archive "$control_sha" src/Bifrost.Concurrency | tar -x -C "$control_root/original"
run original-build dotnet build tools/queue-validation/Validation.csproj -c Release -t:Rebuild \
  "-p:QueueSources=$control_root/original/src/Bifrost.Concurrency" -o "$control_root/original-bin"
run current-build dotnet build tools/queue-validation/Validation.csproj -c Release -t:Rebuild \
  -o "$control_root/current-bin"
for mode in trace trace-ties quality; do
  for version in original current; do
    run "$version-$mode" dotnet "$control_root/$version-bin/Validation.dll" "$mode"
  done
  run "compare-$mode" diff -u "$run_root/logs/original-$mode.log" "$run_root/logs/current-$mode.log"
done
python3 - "$run_root/logs" <<'PY'
import pathlib, sys
logs = pathlib.Path(sys.argv[1])
for mode, expected in [("trace", 36), ("trace-ties", 36), ("quality", 160)]:
    for version in ["original", "current"]:
        lines = (logs / f"{version}-{mode}.log").read_text().splitlines()
        if len(lines) != expected:
            raise SystemExit(f"{version}/{mode}: expected {expected} records, got {len(lines)}")
PY
for version in original current; do
  sources="$repo_root/src/Bifrost.Concurrency"
  if [[ $version == original ]]; then sources="$control_root/original/src/Bifrost.Concurrency"; fi
  run "$version-stripe-build" dotnet build \
    docs/benchmarks/data/api-unbuffered-performance-2026-10-28/stripe-harness/Stripe.csproj \
    -c Release -t:Rebuild "-p:QueueSources=$sources" -o "$control_root/$version-stripe"
done
run benchmark-build dotnet build "$bench_project" -c Release --no-restore -p:BifrostTestHooks=false
run benchmark-list dotnet "$bench_dll" --list flat --filter '*Cpq*'
run benchmark-smoke dotnet "$bench_dll" --job Dry \
  --filter '*CpqSingleThreadedLatencyBenchmarks.MultiQueue_EnqueueDequeue_Int' \
  --artifacts "$run_root/smoke"
# BDN can exit zero after a generated benchmark build fails; inspect its case reports too.
python3 - "$run_root/smoke/results" <<'PY'
import csv, pathlib, sys
reports = list(pathlib.Path(sys.argv[1]).glob("*-report.csv"))
rows = [row for report in reports for row in csv.DictReader(report.open())]
if len(rows) != 4 or any(not row.get("Mean") or row["Mean"] in ("NA", "?") for row in rows):
    raise SystemExit("Dry smoke must execute all four population cases; inspect the retained BDN logs.")
PY
{
  printf 'repo_root=%q\nrun_root=%q\ncontrol_root=%q\nbench_dll=%q\n' \
    "$repo_root" "$run_root" "$control_root" "$bench_dll"
} > "$run_root/ready.env"
echo "Ready: $run_root/ready.env"
echo "72 trace hashes and 160 quality records match. Dry results are not performance evidence."
