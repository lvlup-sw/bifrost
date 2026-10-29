# MultiQueue differential validation

This dependency-free .NET 10 harness compiles either the original or current queue source directly. It deliberately uses internal constructors and a reflection seed reset **only in this executable**; no production RNG controls, synchronization or instrumentation are added. `BIFROST_TEST_HOOKS` is not defined, matching the shipped queue hot path.

From the repository root:

```sh
mkdir -p /tmp/bifrost-baseline
# Review baseline, before API/heap changes:
git archive 6800ae0 src/Bifrost.Concurrency | tar -x -C /tmp/bifrost-baseline
dotnet build tools/queue-validation/Validation.csproj -c Release \
  -p:QueueSources=/tmp/bifrost-baseline/src/Bifrost.Concurrency -o /tmp/bifrost-before
dotnet build tools/queue-validation/Validation.csproj -c Release -o /tmp/bifrost-after

dotnet /tmp/bifrost-before/Validation.dll trace > /tmp/trace-before.txt
dotnet /tmp/bifrost-after/Validation.dll trace > /tmp/trace-after.txt
diff /tmp/trace-before.txt /tmp/trace-after.txt

dotnet /tmp/bifrost-before/Validation.dll quality > /tmp/quality-before.txt
dotnet /tmp/bifrost-after/Validation.dll quality > /tmp/quality-after.txt
diff /tmp/quality-before.txt /tmp/quality-after.txt
```

- `trace`: 36 unique-priority sequential histories across default/custom comparers, 1/4/16 stripes, stickiness 1/4, and buffers 0/1/16. Hashes include selected entries, resident heap/buffer layout, published empty observations, buffer counts, top versions, and final drain. Comparer invocation order is intentionally not a contract.
- `trace-ties`: the same 36 scenarios with priorities drawn from eight values. Hashes include element identities, so changes to equal-priority decisions and final drain are detectable. Compare separately from `trace`.
- `quality`: 80 seeded mixed histories plus their drains (160 records). Populations 64/4096; stripes 4/16; stickiness 1/4; buffers 0/16; five seeds. Reports zero-based rank error and per-element delay: the number of worse-priority entries removed while an entry remains resident. These sequential histories isolate algorithm changes from nondeterministic contention schedules.
- No argument: throughput across populations 16/4096, buffers 0/16, 1/4 threads and 32 stripes. Every thread performs balanced enqueue/dequeue pairs. Each scenario reports the median of three one-second runs following a discarded warmup run. Mops counts enqueue and dequeue separately. Allocation excludes setup and the initial 10,000 pairs per worker; occasional stripe array growth can remain.
- `dense`: only the population-4096, buffer-0, four-thread throughput cell. Uses the same loop and warmup as the full matrix. With normal tiering, starting a fresh process on this cell can produce a different JIT/PGO profile from reaching it after the sparse cells; report both measurements.
- `buffered-single`: only buffer-16, single-thread throughput at populations 16/4096. It retains the full matrix's timing loop and warmup. Report it alongside the full matrix because the preceding workload sequence differs.
- `batch` (run with `DOTNET_TieredCompilation=0`): 31 timed batches of 32,768 enqueue/dequeue pairs after eight warmup batches. Separately measures the whole queue and a single stripe, with integer and preallocated reference payloads, default/custom comparers, populations 16/4096 and buffers 0/16. Timing a batch avoids per-operation Stopwatch overhead. The reported p90 is a percentile of **batch average latencies**, not individual operation latency. Entry creation is outside measurement.
- `batch-sparse` (run with `DOTNET_TieredCompilation=0`): only custom-comparer, population-16, buffer-16 batches for both payload types. No timing or operation changes from `batch`.
- `latency` (run with `DOTNET_TieredCompilation=0`): default/custom comparers and populations 16/4096 with buffers 0/16. Reports whole-queue enqueue/dequeue pair latency and a separate single-stripe critical-section pair proxy, plus allocation after 100,000 warmup pairs. The latter excludes outer lock acquisition, contention and routing, but includes a reentrant push lock and held-root pop. It is **not** a production lock-hold profile. Stopwatch overhead/quantization is present in both builds.

Run performance binaries sequentially on an otherwise idle host in B/A/A/B order. Do not run builds or tests alongside measurements. Repeated medians are descriptive measurements, not a confidence interval. This local harness does not establish high-contention server scaling or concurrent rank distributions. Keep the independent BCL baseline and server benchmarks in `src/Bifrost.Benchmarks` for those comparisons.

See [the validation record](../../docs/benchmarks/2026-10-28-api-remediation.md) for committed results and limitations. The regular tests independently cover failures, conservation, equal priorities, buffered invariants, routing, seqlock publication and concurrency stress.

## Reproduce the performance follow-up

When starting after the [isolated-latency follow-up](../../docs/benchmarks/2026-10-28-api-latency-performance.md), first reverse-apply its [patch](../../docs/benchmarks/data/api-latency-performance-2026-10-28/optimization.patch) inside the temporary source copy. Then follow the older reconstruction steps below. The new evidence index documents the complete F/C/P/O chain.

When starting from the source after the [profiling investigation](../../docs/benchmarks/2026-10-28-api-profile-performance.md), first reverse-apply its [optimization patch](../../docs/benchmarks/data/api-profile-performance-2026-10-28/optimization.patch) with `patch -R -p1` inside the temporary source copy. This reconstructs the retained source from the residual investigation below; older patches must be applied after that step.

Copy `src/Bifrost.Concurrency/*.cs` into a fresh temporary directory. Reverse-apply the [targeted optimization patch](../../docs/benchmarks/data/api-targeted-performance-2026-10-28/optimization.patch) with `patch -R -p1` **inside the copy** to reconstruct the version with the reported 7%/1.7% gaps. To reconstruct the earlier exception-safe implementation, then reverse-apply the [first optimization patch](../../docs/benchmarks/data/api-remediation-performance-2026-10-28/optimization.patch). Build with `QueueSources` pointing to the desired copy. This preserves the same API and tests while isolating each optimization. The original `6800ae0` baseline remains a third comparison; it does not provide the new exception guarantee.

Build all three executables before timing. Run original/safe/optimized/optimized/safe/original sequentially for each measurement mode, with no builds or tests alongside them. Use normal runtime tiering for the original throughput mode, and `DOTNET_TieredCompilation=0` for `batch` and `latency`. Preserve every run, including regressions. See [the follow-up results](../../docs/benchmarks/2026-10-28-api-performance-followup.md).

The [targeted investigation](../../docs/benchmarks/2026-10-28-api-targeted-performance.md) compares original/prior/final in the same balanced order, with three blocks for `dense` and `batch-sparse` and one block for each full matrix. Its raw data includes the runner script and the isolated one-file patch.

The [residual investigation](../../docs/benchmarks/2026-10-28-api-residual-performance.md) evaluates fourteen additional variants and retains no production change. It adds bounded/unbounded failure coverage, duplicate-priority traces and buffered single-thread isolation. Its evidence includes rejected candidates and a fresh full-matrix comparison of original, earlier safe and current sources. Rebuild every control with the same harness revision; the investigation demonstrates that unchanged queue source can produce different tiny latency gaps under different harness revisions.

The [profiling investigation](../../docs/benchmarks/2026-10-28-api-profile-performance.md) adds native stack samples mapped to managed JIT methods, assembly diagnostics, and final balanced comparisons. It retains comparer-first transaction guards and scalar deletion-buffer shifts for reference-free entries of at most eight bytes. Its [profiling driver](../queue-profile/README.md) is separate from this unchanged timing harness.

The [isolated-latency follow-up](../../docs/benchmarks/2026-10-28-api-latency-performance.md) retains accessor and pop-dispatcher inlining, with thread-handle allocation/seeding kept in a cold helper. It repeats the original stripe proxy in both contexts and adds an independent stripe-only diagnostic. The timing harness itself remains unchanged.
