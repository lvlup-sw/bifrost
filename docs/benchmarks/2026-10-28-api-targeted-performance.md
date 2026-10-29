# Targeted MultiQueue performance investigation

Date: 2026-10-28. Follow-up to the [remaining 7% sparse custom-comparer and 1.7% dense four-thread gaps](2026-10-28-api-performance-followup.md).

The [subsequent residual investigation](2026-10-28-api-residual-performance.md) reassesses the remaining sparse and buffered single-thread costs. It retains this production implementation after rejecting further variants with adverse tradeoffs, and expands verification and benchmark coverage.

Sparse buffered custom-comparer pair latency fell **5.35% for value payloads and 4.86% for reference payloads versus the prior version** across six isolated process runs per version. In the original full-matrix ordering, the reductions were **5.91% and 3.39%**; the corresponding gaps to the original fell from **7.17% to 0.83%** and **6.56% to 2.95%**. The isolated cases still show **6.70% and 4.01%** gaps to the original, so the overhead is reduced, not universally eliminated.

The old 1.7% dense unbuffered four-thread deficit was **not stable across reruns**. Final mean throughput was 0.09% below original in six isolated processes and 2.32% above it in the full matrix, with overlapping process ranges. Versus the prior safe version, the differences were -0.17% and -0.11%, respectively. No repeatable dense speedup is claimed. Comparer exception safety and MultiQueue's algorithm are retained.

## Changes and safety argument

The production diff for this investigation is confined to `SubQueue.cs`:

1. **Do not open a buffered rollback transaction when refill cannot compare.** At entry to `PopHeldRoot`, the stripe lock is held and the published stripe count equals `I + D + heap`. When `D` has one entry and total count is at most two, removing that entry leaves at most one entry to refill. The remaining entry is either already the sole heap entry or is pushed from `I` into an empty heap. Neither inserting into an empty heap nor removing its sole entry invokes a comparer. Publication also invokes no comparer. The existing flush/refill code still executes, with identical heap shape, buffer contents and publication. Only the unnecessary inline-buffer snapshots and journal setup are skipped. With two or more remaining entries and an unknown comparer, the full transaction remains.
2. **Check the common cheap conditions first.** Buffered push checks whether `I` is full before classifying the comparer. Buffered pop first checks the existing locked stripe count, then whether `D` will empty, then comparer classification. There is no new cached state, lock, atomic or synchronization protocol. The count is read at the operation boundary, never during a compound mutation before its count update.
3. **Write numeric heap size once.** The primitive default-comparer sift-down receives its new logical length directly. This removes an early `_size` store and reload; the existing final size commit remains. Only the already-known nonthrowing comparer path changes. Unknown-comparer preflight/commit loops, journal recording and rollback are unchanged.

The strong comparer-exception guarantee is retained. This is not a broader allocation-failure guarantee. No two-choice sampling, RNG, stickiness, tie decision, stripe sizing, occupancy fallback, verification scan, buffer policy or publication ordering changed. No direct small-buffer sorting shortcut was introduced. Experiments reusing a span in deletion-buffer insertion did not consistently improve measurements and were discarded.

## Measurement method

Same Apple M3 host (8 cores, 16 GiB), macOS 14.4.1 arm64, .NET SDK 10.0.101/runtime 10.0.1. Three executables compile the same harness with test hooks disabled:

- **Original (O):** `6800ae0`, before the API and exception-safety changes. It does not provide the new exception guarantee.
- **Prior (P):** the optimized implementation from the preceding report, captured before this investigation.
- **Final (N):** the three changes above.

All builds, tests and AOT validation finished before final timing. Each block runs O/P/N/N/P/O sequentially. Full throughput and batch matrices each use one block (two processes per version). The isolated sparse and dense scenarios each use three blocks (six processes per version). Tables report the arithmetic mean of process-level medians, consistent with the preceding report. Ranges are across those process medians, not confidence intervals. All runs are retained, including outliers and regressions.

The throughput modes retain normal runtime tiering, three one-second samples after one discarded warmup, 32 stripes, integer elements/priorities, and one or four workers. Mops/s counts enqueue and dequeue separately. The isolated `dense` filter starts a fresh process directly on population 4096, buffer 0, four threads. In the full matrix, earlier sparse cells execute first. JIT/tiered-profile and workload-order differences are possible; the measurements do not isolate their causal contribution.

Both batch modes disable tiering (`DOTNET_TieredCompilation=0`), use eight warmup batches followed by 31 measured batches of 32,768 enqueue/dequeue pairs, and preallocate payloads. Both integer and reference payloads use integer priorities. The `batch-sparse` filter selects only population 16, buffer 16 and a custom comparer. Single-stripe results and batch-average p90 values are retained in the raw data; they are not production lock-hold times or per-operation p90s.

## Repeated target scenarios

Each cell is the mean **[minimum–maximum]** across six process medians.

| Scenario | Original | Prior | Final | Final vs prior | Final vs original |
|---|---:|---:|---:|---:|---:|
| Sparse custom, value (ns/pair) | 62.63 [62.09–63.28] | 70.60 [70.12–71.32] | 66.83 [66.26–67.08] | -5.35% | +6.70% |
| Sparse custom, reference (ns/pair) | 68.99 [68.65–69.61] | 75.42 [74.99–75.89] | 71.76 [71.16–72.19] | -4.86% | +4.01% |
| Dense unbuffered, four threads (Mops/s) | 36.306 [35.217–36.953] | 36.337 [35.925–36.673] | 36.273 [36.093–36.622] | -0.17% | -0.09% |

The dense process-median aggregates were 36.458 / 36.325 / 36.186 Mops/s for O/P/N, respectively: final is 0.74% below original using this alternative aggregation. That differs from the 0.09% mean gap and reinforces the need to report the distribution rather than choose the most favorable statistic.

## Full-matrix cross-check

The isolated results are checked against the original full-matrix ordering. Latency is ns per enqueue/dequeue pair (lower is better); throughput is Mops/s (higher is better). Percentage changes use the same-run-set means, not the historical measurements.

| Population | Buffer | Threads | O | P | N | N vs P | N vs O |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 16 | 0 | 1 | 43.210 | 44.739 | 44.567 | -0.38% | +3.14% |
| 16 | 0 | 4 | 24.802 | 24.434 | 24.468 | +0.14% | -1.35% |
| 16 | 16 | 1 | 43.263 | 43.978 | 43.210 | -1.75% | -0.12% |
| 16 | 16 | 4 | 22.543 | 22.872 | 23.014 | +0.62% | +2.09% |
| 4096 | 0 | 1 | 30.930 | 31.346 | 31.444 | +0.31% | +1.66% |
| 4096 | 0 | 4 | 36.370 | 37.257 | 37.216 | -0.11% | +2.32% |
| 4096 | 16 | 1 | 33.121 | 32.855 | 32.404 | -1.37% | -2.16% |
| 4096 | 16 | 4 | 31.202 | 31.357 | 31.427 | +0.22% | +0.72% |

| Payload | Comparer | Population | Buffer | O ns | P ns | N ns | N vs P | N vs O |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| value | default | 16 | 0 | 60.86 | 61.51 | 61.69 | +0.28% | +1.36% |
| value | default | 16 | 16 | 63.27 | 62.74 | 62.67 | -0.10% | -0.93% |
| value | default | 4096 | 0 | 71.47 | 72.50 | 73.25 | +1.03% | +2.49% |
| value | default | 4096 | 16 | 74.75 | 74.55 | 75.20 | +0.87% | +0.61% |
| value | custom | 16 | 0 | 63.50 | 63.94 | 63.97 | +0.05% | +0.73% |
| value | custom | 16 | 16 | 63.82 | 68.40 | 64.36 | -5.91% | +0.83% |
| value | custom | 4096 | 0 | 104.20 | 110.55 | 109.98 | -0.51% | +5.55% |
| value | custom | 4096 | 16 | 87.33 | 86.10 | 86.18 | +0.09% | -1.32% |
| reference | default | 16 | 0 | 63.22 | 63.39 | 63.73 | +0.54% | +0.81% |
| reference | default | 16 | 16 | 67.09 | 68.53 | 68.47 | -0.10% | +2.04% |
| reference | default | 4096 | 0 | 78.20 | 79.87 | 79.97 | +0.14% | +2.27% |
| reference | default | 4096 | 16 | 75.97 | 76.00 | 76.53 | +0.68% | +0.74% |
| reference | custom | 16 | 0 | 65.65 | 67.13 | 66.73 | -0.60% | +1.65% |
| reference | custom | 16 | 16 | 68.93 | 73.45 | 70.96 | -3.39% | +2.95% |
| reference | custom | 4096 | 0 | 119.75 | 127.18 | 126.06 | -0.88% | +5.27% |
| reference | custom | 4096 | 16 | 93.81 | 94.48 | 94.48 | -0.00% | +0.71% |

## Remaining tradeoffs

The broader matrix also contains costs: default-comparer buffered single-thread throughput is 1.75% lower than prior in the sparse case and 1.37% lower in the dense case (the latter is 2.16% below original). Default buffered batch latencies are almost unchanged to 0.87% higher versus prior. These observations are retained rather than claiming every workload became faster. A final classifier-first guard experiment produced mixed throughput results and batch latency changes from -1.25% to +0.81%; it was not retained. Its complete F/C/C/F comparison is in `exploratory/c6-check-*` (F is the retained final version, C is that alternate candidate).

Dense unbuffered custom-comparer pair latency remains 5.55% (value) and 5.27% (reference) above original in this full-matrix rerun, despite being slightly faster than prior. Safety bookkeeping remains necessary where comparisons can throw. Absolute gaps differ from the previous report, so conclusions about this change use contemporaneous O/P/N comparisons.

Host load, CPU frequency and placement on heterogeneous cores are uncontrolled. These are local descriptive comparisons; the overlapping ranges do not establish statistical significance or guarantee a speedup. In particular, the earlier 1.7% concurrent deficit is not a stable estimate across reruns and execution orders. The redundant store removal is a small local simplification, not evidence that every four-thread workload improves.

Arbitrary comparers still pay for preflight/commit sifts and compound buffered rollback whenever comparisons can throw. There is no new per-operation transaction allocation. Raw allocation counts include retained heap/journal backing-storage growth after warmup; unbounded storage growth is not promised allocation-free. The existing journal reference-clearing policy is unchanged.

High-core-count server contention, long/reference priorities, expensive comparers, end-to-end dispatch and concurrent rank distributions remain workload-specific release checks. The original algorithm trace and rank/delay matches validate sampled deterministic histories, not every concurrent interleaving or a universal rank bound.

## Verification and reproduction

- Full Release solution build: **zero warnings/errors**.
- **1,212 tests passed:** 131 concurrency, 795 main, 286 scheduling; no skips or relaxed assertions. This includes all six exhaustive comparer-failure configurations (buffers 0/1/16, explicit custom comparer and throwing user-defined default priority), conservation, capacity, publication and stress checks.
- Four new boundary cases cover buffers 1/16: single-entry refill succeeds without invoking an armed throwing comparer, and two-entry refill throws while restoring counts, contents and top publication. The buffer-1 test also checks a sole remaining heap entry.
- **36/36 trace hashes and 160/160 rank/delay records match the original baseline**, including buffering, stripe-count and stickiness variants.
- Concurrency Native AOT smoke published and ran successfully, conserving 4,096 buffered reference entries. A sandboxed publish stalled and was stopped; the retry used local process communication and a single MSBuild node. The existing SourceLink dependency audit workaround (`NuGetAudit=false` on this publish command only) remains documented; no project audit suppression was added.
- The new reverse patch was applied to a temporary source copy and matched the prior snapshot byte-for-byte. Applying the earlier reverse patch afterward also reconstructed the preceding safe implementation byte-for-byte.

[Raw final results, exploratory results, verification logs, source hashes, runner, summary script and the reversible patch](data/api-targeted-performance-2026-10-28/) are retained. The final runner accepts a directory containing `original/Validation.dll`, `prior/Validation.dll` and `final/Validation.dll`. See the [harness instructions](../../tools/queue-validation/README.md) for source reconstruction and build commands.
