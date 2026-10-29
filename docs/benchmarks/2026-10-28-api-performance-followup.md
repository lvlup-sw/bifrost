# Comparer-safe MultiQueue performance follow-up

Date: 2026-10-28. This follows the [API remediation report](2026-10-28-api-remediation.md) and supersedes its performance numbers for the optimized implementation. The strong comparer-exception guarantee and the MultiQueue selection algorithm are retained.

The [subsequent targeted investigation](2026-10-28-api-targeted-performance.md) further reduces sparse custom-comparer overhead and examines the repeatability of the dense four-thread result. The numbers below describe the preceding revision; use the newer report for the current implementation and remaining tradeoffs.

The largest original throughput regression is recovered in this rerun: sparse buffered single-thread throughput rose **6.8% versus the previous safe version**, to 1.1% above the original baseline. Dense unbuffered custom-comparer pair latency fell **9.5%** (121.04 to 109.53 ns), reducing its gap against the original from 13.5% to 2.7%. Remaining measured costs include about **7%** in sparse buffered custom-comparer latency and **1.7%** in dense unbuffered four-thread throughput versus the original. These are local measurements with the limitations below.

## What changed and why

Generated-code inspection and controlled candidate comparisons led to these changes:

1. **Inline comparer classification.** The JIT had left `HasNonThrowingComparer` as an out-of-line call, including in numeric hot paths. Inlining its accessor lets closed numeric instantiations reduce the type tests to a comparer-field check. The same explicit whitelist is used; arbitrary comparers and user-defined `CompareTo` implementations still take the safe path. For `SubQueue<int,int>` with tiering disabled, the selected candidate's `PopHeldRoot` code shrank from 244 to 152 bytes and `UnbufferedPopHeldRoot` from 436 to 344 bytes after this change.
2. **Reuse the sift's root decision.** Heap insertion now returns whether it reached index zero. That already establishes whether the root changed strictly, so unbuffered insertion no longer makes a separate comparer call for publication. Equal-priority publication behavior is unchanged. Comparator invocation counts are not an API guarantee; comparers must remain consistent and non-reentrant.
3. **Keep transaction work out of ordinary copy loops.** Test the stripe-local journal flag once before committing a compared sift path. Non-transactional commits copy directly; buffered transactions still record every overwritten slot. Journal recording/growth is in a non-inlined helper, keeping that code out of each inlined write.
4. **Hoist repeated checks.** Compute the last parent once per sift; select the default/custom comparer loop once for deletion-buffer insertion; cache the custom comparer for ascent. No comparisons that can throw move after a commit.
5. **Initialize thread state before capacity reservation.** This keeps initialization outside the protected retry loop without leaking a reservation on initialization failure. A cold thread's first rejected bounded enqueue can now initialize thread-local state even if capacity is full; warm-thread admission and hard bounds are unchanged.

Experiments that replaced the stack path with packed choices or reconstructed it from parent indices reduced code size but did not consistently improve whole-queue latency. They were discarded. Forcing the entire heap push/pop methods to inline also failed to improve results consistently. The final implementation keeps the stack-based preflight and original numeric single-pass sifts; heap push/pop retain default inlining heuristics while comparer classification is explicitly inlined.

No routing, two-distinct-choice selection, tie decision, stickiness, stripe sizing, occupancy fallback, verification scan, buffer policy, or publication protocol changed. There is no new shared lock, shared atomic or heap-wide copy. All potentially throwing comparisons still finish before the corresponding sift commits; compound buffered failures still roll back with the same reusable per-stripe journal.

## Method

Same Apple M3 host (8 cores, 16 GiB), macOS 14.4.1 arm64, .NET SDK 10.0.101/runtime 10.0.1. Three executables use the same harness with test hooks disabled:

- **Original (O):** `6800ae0`, before the exception-safety/API changes. This version does not satisfy the new exception guarantee.
- **Previous safe (S):** the implementation at the start of this performance follow-up.
- **Optimized (N):** this change.

Final timing order was O/S/N/N/S/O for each mode, after all builds, tests and AOT validation finished. Every result is retained, including regressions. Tables average the two process-level medians; latency means average the two process means. This is a local descriptive comparison, not a statistically powered performance guarantee. Other host activity, CPU frequency and placement on heterogeneous cores remain uncontrolled.

The original throughput harness uses normal tiering, three one-second samples after a discarded warmup, and 32 stripes. The new batch mode disables tiering, preallocates payloads, warms eight batches, and times 31 batches of 32,768 balanced enqueue/dequeue pairs. It separately measures whole-queue and single-stripe pairs. Batch timing reduces the roughly 42 ns Stopwatch quantization and per-operation measurement overhead. Its p90 is a percentile of batch-average latencies, not an individual-operation p90. Both value and reference payloads use `int` priorities; this does not establish results for every priority/comparer type.

## Throughput

Mops/s counts enqueue and dequeue separately; higher is better.

| Population | Buffer | Threads | Original | Previous safe | Optimized | vs safe | vs original |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 16 | 0 | 1 | 42.73 | 43.19 | 44.68 | +3.4% | +4.6% |
| 16 | 0 | 4 | 24.43 | 24.43 | 24.48 | +0.2% | +0.2% |
| 16 | 16 | 1 | 43.27 | 40.98 | 43.77 | +6.8% | +1.1% |
| 16 | 16 | 4 | 22.79 | 22.80 | 22.88 | +0.4% | +0.4% |
| 4096 | 0 | 1 | 31.02 | 31.06 | 31.35 | +0.9% | +1.1% |
| 4096 | 0 | 4 | 37.21 | 37.41 | 36.59 | -2.2% | -1.7% |
| 4096 | 16 | 1 | 32.84 | 31.90 | 32.67 | +2.4% | -0.5% |
| 4096 | 16 | 4 | 31.45 | 31.53 | 31.73 | +0.6% | +0.9% |

## Batched pair latency

Nanoseconds per enqueue/dequeue pair; lower is better. Reference payloads are created before timing. Detailed single-stripe timing, p90 and allocation counts are in the raw results.

| Payload | Comparer | Population | Buffer | Original ns | Safe ns | Optimized ns | vs safe | vs original |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| value | default | 16 | 0 | 61.20 | 65.30 | 61.33 | -6.1% | +0.2% |
| value | default | 16 | 16 | 62.62 | 65.08 | 62.52 | -3.9% | -0.2% |
| value | default | 4096 | 0 | 71.80 | 75.48 | 72.47 | -4.0% | +0.9% |
| value | default | 4096 | 16 | 75.37 | 79.01 | 75.09 | -5.0% | -0.4% |
| value | custom | 16 | 0 | 64.22 | 68.42 | 63.51 | -7.2% | -1.1% |
| value | custom | 16 | 16 | 63.85 | 68.22 | 68.35 | +0.2% | +7.0% |
| value | custom | 4096 | 0 | 106.66 | 121.04 | 109.53 | -9.5% | +2.7% |
| value | custom | 4096 | 16 | 87.53 | 89.65 | 86.77 | -3.2% | -0.9% |
| reference | default | 16 | 0 | 63.97 | 67.00 | 64.03 | -4.4% | +0.1% |
| reference | default | 16 | 16 | 67.56 | 70.10 | 68.33 | -2.5% | +1.1% |
| reference | default | 4096 | 0 | 78.68 | 82.37 | 79.90 | -3.0% | +1.6% |
| reference | default | 4096 | 16 | 76.43 | 79.09 | 75.28 | -4.8% | -1.5% |
| reference | custom | 16 | 0 | 65.95 | 71.22 | 66.46 | -6.7% | +0.8% |
| reference | custom | 16 | 16 | 68.75 | 74.98 | 73.26 | -2.3% | +6.6% |
| reference | custom | 4096 | 0 | 120.72 | 132.88 | 125.19 | -5.8% | +3.7% |
| reference | custom | 4096 | 16 | 94.22 | 96.81 | 94.14 | -2.8% | -0.1% |

The original per-operation latency mode was also rerun. Its optimized-versus-safe mean changes ranged from -10.1% to -3.0%; those short timings are more sensitive to quantization and scheduling than the batched results. They remain in the raw data rather than being discarded when they differ from the batch measurements.

## Remaining tradeoffs and limits

The safety guarantee still requires preflight/commit work for arbitrary comparers and rollback bookkeeping for compound buffered mutations. Some cells remain slower than the original unsafe implementation; the tables quantify those costs rather than treating reduced code size as proof of performance equivalence. Small concurrent-throughput differences should be read alongside the two individual runs, not as a universal speedup or regression.

There is no new per-operation transaction allocation. Most post-warmup cells report zero bytes; dense cases still show occasional retained backing-storage growth, including the custom-comparer journal. The journal's growth/reference-clearing policy is unchanged. An unbounded queue is not promised allocation-free as its storage requirements grow.

The final numeric inlining result is runtime- and architecture-dependent. Actual server contention, long/reference priorities, expensive comparers, end-to-end work-dispatch latency and production lock-hold profiles remain workload-specific release checks. The single-stripe measurements are microbenchmarks, not measured lock-hold distributions under contention. This local eight-core comparison does not validate the earlier 64-thread server scaling claims.

## Correctness and runtime verification

- **36/36 trace hashes match the original baseline**, including default/custom comparers, stripes 1/4/16, stickiness 1/4, buffers 0/1/16, selected entries, per-operation stripe/buffer/publication state and final drain.
- **160/160 rank/delay records match** across sparse/dense mixed histories and drains, five seeds, buffering and stickiness variants.
- Expanded exhaustive comparer-failure injection now also covers a user-defined value priority whose default comparer invokes a throwing `CompareTo`. All existing storage, reservation, publication, buffer-count, recovery, clear/refill and capacity checks remain.
- **1,208 tests passed:** 127 concurrency (including stress and six comparer-failure configurations), 795 main, 286 scheduling. No skips or relaxed thresholds.
- Full Release solution build: zero warnings/errors. Optimized concurrency Native AOT smoke published and executed successfully. AOT uses the previously documented local `NuGetAudit=false` workaround for the existing SourceLink dependency advisory; no project audit suppression was added.

[Raw measurements, verification logs and the reversible optimization patch](data/api-remediation-performance-2026-10-28/) are retained. [Harness instructions](../../tools/queue-validation/README.md) explain how to reconstruct the previous safe source in a temporary copy and rerun all three versions.
