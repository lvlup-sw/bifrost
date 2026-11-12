# Priority API PR: reproduce and extend the Xeon benchmark report

This runbook covers PR #69 and its benchmark/evidence layer, PR #70. It explains how to regenerate the archived figures, collect a fresh server result, and check the small comparer-safety and buffering tradeoffs investigated in October. The reference report is [the Xeon 8573C study](2026-06-13-cpq-xeon-8573c.md). Its filename says June 13, but the report and main CSV record June 15, 2026.

The complete toolkit is merged into `main` and included in `v0.6.0`. PR #69 contains the existing BenchmarkDotNet and server-throughput harnesses. PR #70 adds the source-direct validation/profiling tools and this guide. Record the actual commit SHA when running; branch names can move.

The [completed October Xeon campaign](data/azure-d64sv6-2026-10-28/README.md) includes four server sweeps, repeated ShortRun jobs, and balanced implementation-control comparisons on the matching 8573C host.

## Automated preparation

From a checkout with the .NET 10 SDK, run:

```bash
# The historical control may not be present after a fresh clone of the merged PRs.
git fetch origin 6800ae01b59bacff024821f69f62bbce4b98fe7d
bash scripts/benchmarks/prepare-cpq.sh /tmp/bifrost-cpq-ready
source /tmp/bifrost-cpq-ready/ready.env
```

The output directory must be new and outside the repository, so its input archive cannot include its own output. The [preparation script](../../scripts/benchmarks/prepare-cpq.sh) captures the commit, tracked patch, added inputs, runtime, topology, affinity, CPU policy and resolved packages. It builds the solution, runs regular and stress tests, builds original/current validation and stripe executables, compares all 72 trace hashes and 160 rank/delay records, and executes the four-population BenchmarkDotNet Dry smoke. Failures retain their logs and stop preparation. `ready.env` is written only after all checks pass and supplies the shell variables used below.

This prepares the host and binaries; it does not collect publishable performance results or create cloud resources. Dry timings are execution checks only. Review the captured host details against section 2, record VM size/region manually when applicable, and use an idle host for the timed campaign. Rerun preparation on the actual measurement host; binaries and readiness from a workstation do not validate the Xeon setup.

## 1. Choose the comparison

| Question / original figure | Tool or input | What it measures |
|---|---|---|
| Regenerate all eleven archived figures | Archived CSVs and `generate_charts.py` | Existing June measurements; no new benchmark |
| Scaling, speedup, per-doubling gain: figures 1, 4, 5 | `throughput` | Three targets × four workloads × thread ladder |
| Stickiness: figures 2, 3 | `stickiness`, plus `throughput` baselines | `s=1,2,4,8` at one and two threads |
| Single-thread latency: figure 6 | `CpqSingleThreadedLatencyBenchmarks` pair methods | Nanoseconds per enqueue/dequeue pair and allocated bytes |
| Relaxation-cost illustration: figure 7 | Historical analytical chart; `Validation.dll quality` for measured quality | Theory and seeded measurements are different evidence |
| Occupancy bitmask: figure 8 | Archived before/after data or separately reconstructed controls | No supported runtime switch disables the bitmask |
| Buffering: figures 9, 10 | `buffered-throughput`, `CpqBufferedAbBenchmarks` | Buffer capacities 0 and 16 |
| Heap arity: figure 11 | Archived arity-4/arity-8 experiment | No public arity switch; do not change production arity to recreate a chart |
| This PR's sparse/custom-comparer and reference-stripe tradeoffs | `tools/queue-validation` and the independent stripe harness | Same-harness, source-controlled before/after comparisons |

### Interpret the current API correctly

- The CSV target `MultiQueueDequeueMin` is a legacy label. Its adapter now calls `TryDequeueBestObserved`: an O(n) best-observed scan, **not a globally exact concurrent minimum**. Use that description in new figures and captions.
- `LockingBaseline` / `Locking_*` now use the benchmark project's private synchronized BCL `PriorityQueue`. The removed public `LockingPriorityQueue` is not restored. Historical descriptions of a custom binary heap do not describe the current baseline implementation.
- `RawLocked_*` is the lock-wrapped BCL reference; `RawUnlocked_*` is an unsynchronized control. Do not use the latter as the thread-safe comparison.
- The report's approximate rank-error formula is model context, not an implementation guarantee. Stickiness, buffering, sampling and fallback affect applicability. Seeded sequential quality results do not establish a concurrent rank distribution.
- Exact orchestrator dispatch remains internal. These queue-level benchmarks do not measure handler execution, admission-to-completion latency, or the complete orchestrator.

## 2. Prepare the host and capture provenance

For a comparable server update, use the original class of host: Azure `Standard_D64s_v6`, Xeon Platinum 8573C, 64 logical / 32 physical cores, one NUMA node, Ubuntu 24.04 x64. Verify the provisioned machine rather than assuming the SKU guarantees the same CPU topology. The original report used .NET 10.0.9, Workstation GC and BenchmarkDotNet 0.14.0. Record differences if you use another runtime. ARM64 laptop measurements extend the report; they do not reproduce its Xeon scaling claims.

Run these Bash commands from the repository root. Use one shell for the variables below. No cloud resources are created by this guide.

```bash
set -euo pipefail
repo_root="$PWD"
run_root="$(mktemp -d /tmp/bifrost-cpq.XXXXXX)"
bench_project="$repo_root/src/Bifrost.Benchmarks/Bifrost.Benchmarks.csproj"
bench_dll="$repo_root/src/Bifrost.Benchmarks/bin/Release/net10.0/Bifrost.Benchmarks.dll"
git rev-parse HEAD > "$run_root/commit.txt"
git status --short > "$run_root/worktree-status.txt"
git diff --binary HEAD > "$run_root/worktree.patch"
dotnet --info > "$run_root/dotnet-info.txt"
dotnet --list-runtimes > "$run_root/runtimes.txt"
uname -a > "$run_root/uname.txt"
# Linux server topology and affinity:
lscpu > "$run_root/lscpu.txt"
taskset -pc $$ > "$run_root/affinity.txt"
env | sort | sed -n '/^DOTNET_/p; /^COMPlus_/p' > "$run_root/runtime-env.txt"
dotnet restore "$bench_project"
dotnet build "$bench_project" -c Release --no-restore
dotnet "$bench_dll" --list flat --filter '*Cpq*' > "$run_root/benchmark-list.txt"
```

Use a clean checkout for publishable comparisons, or preserve the complete patch and all added inputs. Also record the VM size/region, CPU governor or power policy, container CPU quota, SDK, resolved package versions and GC mode from the harness output. On macOS, substitute `sysctl -n machdep.cpu.brand_string hw.physicalcpu hw.logicalcpu` for `lscpu`; Linux affinity commands do not apply there.

Leave processor-count overrides unset for the historical server comparison. The queue defaults to `RoundUpPow2(4 × Environment.ProcessorCount)` stripes: 256 on the original host. The throughput ladder reaches **twice** the reported processor count, so that host produces 128-thread oversubscription rows even though the original figures stop at 64. Keep those rows and label oversubscription separately. Setting `DOTNET_PROCESSOR_COUNT` changes queue sizing as well as the sweep; it is not CPU pinning.

Build every comparison executable before timing. Run one benchmark process at a time on an otherwise idle machine. Keep builds, tests, profilers and other benchmark jobs out of the measurement window.

## 3. Recreate the archived figures without rerunning benchmarks

The archived chart generator uses only the Python standard library. It reads sibling CSVs, including the `ab/` controls, and writes eleven SVGs next to itself. Work in a copy to preserve the evidence:

```bash
archive_copy="$run_root/june-archive"
cp -R docs/benchmarks/data/azure-d64sv6-2026-06-13 "$archive_copy"
python3 "$archive_copy/generate_charts.py"
```

This regenerates the archived figures, not their experimental controls. The generator contains historical hardware labels, fixed axis limits, 64-thread endpoints and numerical conclusions in captions. Do not publish its output over fresh CSVs without updating those annotations and checking every figure.

## 4. Collect fresh server throughput and latency

### Fixed-window throughput

The dedicated verbs take positional `windowSeconds` and `outputDirectory` arguments. They are not BenchmarkDotNet jobs; `--job` and `--filter` do not apply to them.

```bash
for repeat in 1 2 3 4; do
  out="$run_root/current-$repeat"
  mkdir -p "$out"
  dotnet "$bench_dll" throughput 3 "$out/scaling" > "$out/scaling.log" 2>&1
  dotnet "$bench_dll" stickiness 3 "$out/stickiness" > "$out/stickiness.log" 2>&1
  dotnet "$bench_dll" buffered-throughput 3 "$out/buffered" > "$out/buffered.log" 2>&1
done
```

The four workloads are `UniformMixed5050`, `SplitProducerConsumer`, `NarrowKeyRange` and `Drain`. Mixed workloads start with 100,000 entries. Narrow keys span 1,000 priorities. Drain preloads approximately one million entries across workers, then divides successful removals by actual elapsed drain time; its three-second value is a watchdog, not the throughput denominator. Mixed and split workloads count dequeue **attempts**, including unsuccessful attempts, alongside enqueues. These rates are not all successful-delivery rates.

Outputs are `throughput.csv/.md`, `throughput-stickiness.csv/.md`, and `throughput-buffered-ab.csv/.md`. Each CSV carries environment comments. The ordinary sweep has stickiness 1 and buffering off; the buffering sweep compares 0/16 at stickiness 1. Payload and priority are both `long`. The standalone server sweep has no separate discarded warmup phase, so retain run order and repeats. A same-host comparison between revisions should alternate complete processes in A/B/B/A blocks rather than run all A trials before all B trials.

### BenchmarkDotNet latency and allocation

Use pair methods for fixed-population latency. Enqueue-only methods grow the heap throughout a run and cannot support the same population-axis claim.

```bash
# Discovery/execution smoke only; Dry timings are not performance evidence.
dotnet "$bench_dll" --job Dry \
  --filter '*CpqSingleThreadedLatencyBenchmarks.MultiQueue_EnqueueDequeue_Int' \
  --artifacts "$run_root/smoke"

# June-compatible ShortRun measurements, including the sparse population-10 rows.
dotnet "$bench_dll" --job Short \
  --filter '*CpqSingleThreadedLatencyBenchmarks.*EnqueueDequeue*' \
  --artifacts "$run_root/latency"
dotnet "$bench_dll" --job Short \
  --filter '*CpqBufferedAbBenchmarks*' \
  --artifacts "$run_root/buffered-latency"
```

Both classes cover populations 10, 1,000, 100,000 and 1,000,000. The buffering class covers capacities 0/16 and both `(int,int)` and `(string,long)` entry shapes. The single-thread class also has a string-priority case; that is a different comparer path, not the reference-element/integer-priority case.

Preserve the complete BenchmarkDotNet artifact directories, especially `results/*-report.csv`, `*-report-github.md` and logs. Check `Allocated` for steady-state pair rows; the historical 0 B/op statement does not apply to construction, growth or snapshots. Mean is time per pair, not time per individual queue operation. ShortRun reproduces the old protocol but is insufficient by itself to establish a roughly 1% change. Use repeated independent runs or a longer BDN job for close comparisons, and retain its configuration and uncertainty.

## 5. Validate this PR's algorithm, safety and performance tradeoffs

The source-direct harness allows the **same harness revision** to compile different queue revisions. It has no production test hooks. The pre-change control `6800ae0` lacks the new comparer exception guarantee, so its speed alone cannot justify reverting safety logic.

```bash
control_root="$run_root/controls"
mkdir -p "$control_root/original"
git archive 6800ae01b59bacff024821f69f62bbce4b98fe7d src/Bifrost.Concurrency \
  | tar -x -C "$control_root/original"
dotnet build tools/queue-validation/Validation.csproj -c Release -t:Rebuild \
  -p:QueueSources="$control_root/original/src/Bifrost.Concurrency" \
  -o "$control_root/original-bin"
dotnet build tools/queue-validation/Validation.csproj -c Release -t:Rebuild \
  -o "$control_root/current-bin"

for mode in trace trace-ties quality; do
  dotnet "$control_root/original-bin/Validation.dll" "$mode" > "$control_root/original-$mode.txt"
  dotnet "$control_root/current-bin/Validation.dll" "$mode" > "$control_root/current-$mode.txt"
  diff -u "$control_root/original-$mode.txt" "$control_root/current-$mode.txt"
done
```

The expected comparison covers 72 unique/tied-priority trace hashes and 160 rank/delay records. An unexpected difference requires investigation; do not update the expected output merely to accept a faster candidate. These sequential traces complement concurrency and exception tests; they do not replace them.

Run the repository's tests before timing:

```bash
dotnet build src/Bifrost.sln -c Release
for project in Bifrost.Tests Bifrost.Tests.Scheduling Bifrost.Tests.Concurrency; do
  dotnet run --project "src/$project/$project.csproj" -c Release --no-build -- \
    --treenode-filter '/*/*/*/*[Category!=Stress]'
done
dotnet run --project src/Bifrost.Tests.Concurrency -c Release --no-build -- \
  --treenode-filter '/*/*/*/*[Category=Stress]'
```

Then collect both full and isolated timing contexts:

```bash
for block in 1 2 3; do
  slot=0
  for version in original current current original; do
    slot=$((slot + 1))
    dll="$control_root/$version-bin/Validation.dll"
    prefix="$run_root/validation-$block-$slot-$version"
    dotnet "$dll" > "$prefix-throughput.txt"
    dotnet "$dll" buffered-single > "$prefix-buffered-single.txt"
    DOTNET_TieredCompilation=0 dotnet "$dll" batch > "$prefix-batch.txt"
    DOTNET_TieredCompilation=0 dotnet "$dll" batch-sparse > "$prefix-batch-sparse.txt"
  done
done
```

Throughput uses normal tiering and reports Mops/s with enqueue and dequeue counted separately. Batch modes disable tiering and report ns/pair; their p90 is a percentile of **batch averages**, not per-operation tail latency. The full batch matrix includes default/custom comparers, value/reference payloads, populations 16/4096 and buffers 0/16. Keep the full matrix: running an isolated cell in a fresh process can change JIT/PGO context.

For the remaining reference-stripe question, also build the independent diagnostic:

```bash
dotnet build docs/benchmarks/data/api-unbuffered-performance-2026-10-28/stripe-harness/Stripe.csproj \
  -c Release -t:Rebuild -p:QueueSources="$repo_root/src/Bifrost.Concurrency" \
  -o "$control_root/current-stripe"
DOTNET_TieredCompilation=0 dotnet "$control_root/current-stripe/Stripe.dll" reference \
  > "$run_root/current-stripe.txt"
```

Build each comparison control with `-p:QueueSources=/absolute/control/source/directory`, then use the same balanced process order. The important full-matrix row is `payload=reference custom=True population=4096 c=0`. The stripe-only diagnostic excludes whole-queue routing and is not a production lock-hold measurement.

To specifically verify preservation of the October gains, compare current against the pre-inlining and pre-copy/guard controls, not just `6800ae0`. Follow the exact reverse-patch sequence and source hashes in the [unbuffered investigation evidence](data/api-unbuffered-performance-2026-10-28/README.md). Reconstruct controls only in scratch copies. The recorded 4.3%/7.6% buffered gains used the pre-copy/guard control; they are not speedups over the June lock baseline. The latest residual stripe gap was context-sensitive: 1.2% slower in full matrices but 2.2% faster in the independent diagnostic. A new report must preserve that distinction until new evidence replaces it.

Use [the profiling driver](../../tools/queue-profile/README.md) only in separate diagnostic runs. Instrumented elapsed times and sample percentages are not benchmark results. Do not weaken rollback, comparison preflight, publication, reference clearing or capacity-reservation guarantees to improve a timing result.

## 6. Update charts and publish an extension

Create a new dated directory under `docs/benchmarks/data/`, with separate subdirectories for each revision and repeat. Do not replace June raw data or silently mix old bitmask/arity controls with new runtime measurements.

For each plotted series, aggregate matching rows across completed runs by target, workload, thread count, stickiness and buffer capacity. Preserve per-run data, report sample counts and spread, and retain failed/aborted-run logs with reasons. Keep CSV environment comments out of CSV parsing (`csv.DictReader` over non-`#` lines). Do not concatenate repeats directly into the historical generator: its dictionary loaders overwrite earlier duplicate keys rather than aggregate them.

Use a copy of the June generator as a plotting starting point. Its current input names are:

| Input | Fresh source |
|---|---|
| `throughput.csv` | Aggregated scaling CSV |
| `throughput-stickiness.csv` | Aggregated stickiness CSV |
| `cpq-singlethreaded-latency-report.csv` | Selected/aggregated BDN pair report, retaining `Method`, `Population`, `Mean` |
| `ab/buffered/throughput-buffered-ab.csv` | Aggregated buffering CSV |
| `ab/buffered/buffered-latency-report.csv` | BDN buffer report, retaining `Method`, `Population`, `BufferCapacity`, `Mean` |
| `ab/bitmask-before/latency-report.csv`, `ab/arity8/throughput.csv` | Only newly verified historical controls, if actually rerun |

If you lack fresh bitmask/arity controls, remove their loading and figure calls in the copied generator. Publish figures 1–6 and 9–10 with fresh inputs; retain figure 7 only as explicitly labeled model context. Keep figures 8/11 as links to the historical study, not newly measured improvements. Adapt the parser if BDN changes units or schema: the June loader expects `Mean` in nanoseconds. Update hard-coded axis limits so improved results are not clipped, thread endpoints, hardware/runtime captions, `BestObserved` naming, baseline descriptions, and all numerical assertions. Inspect the SVGs after generation.

The new report should include:

1. Exact source and harness commits, complete environment, settings, comparison controls and run order.
2. A workload matrix, raw-data links, aggregation script and commands sufficient to reproduce every table.
3. Same-host changes in latency and throughput, with denominators and operation units stated. Compare speedup ratios only within a consistent host/runtime/harness setup.
4. Allocations, trace/quality comparisons, exception-safety tests, stress results and any failed verification.
5. Separate sections for measured performance, theoretical quality context, remaining tradeoffs and unmeasured cases.

Useful extensions are expensive custom comparers, reference priorities, bounded admission, equal-priority workloads, oversubscription, additional NUMA topologies and end-to-end orchestrator latency. Add independent benchmark cases with explicit payload allocation, population, warmup, failure counting and success criteria. Keep MultiQueue's sampling, stickiness, buffering and heap policies fixed when evaluating this PR's implementation cost; algorithm experiments need separately named controls and quality measurements.

## Validation of this guide

The commands and names were checked against the current harness source and benchmark discovery. A BenchmarkDotNet Dry smoke executed all four population cases of `MultiQueue_EnqueueDequeue_Int` and wrote CSV/Markdown artifacts. All eleven archived SVGs regenerated byte-for-byte in a scratch copy and parsed as XML. The documented source-direct original/current comparison matched all 72 trace hashes and 160 rank/delay records, and the independent stripe diagnostic built successfully. The five XML-documentation review fixes passed a Release solution build with zero warnings/errors and leave executable source unchanged. A fresh Xeon performance campaign is still required for new server numbers; this guide does not claim one was run.
