# MultiQueue isolated-latency follow-up — 2026-10-28

The retained change removes two small method-call boundaries: inline the per-operation thread-handle accessor while keeping first-use allocation/seeding in a cold helper, and inline the small `PopHeldRoot` dispatcher into its locked callers. The prior comparer-first guards and compact-value scalar copies remain. No cached transaction flags, eager guard evaluation or new queue state are retained.

The isolated value-payload latency returns to the original baseline in these measurements; the reference-payload gap is substantially smaller. The original reference stripe proxy's regression is recovered. Buffered throughput is checked against both the start-of-turn source and the source before the preceding copy/guard improvements. This report distinguishes those results from a separate stripe-only diagnostic, which does not show a general reference speedup.

## Evidence and changes

The previous profiles showed frequent calls through `ThreadHandle.Current` and the safe pop dispatcher. Fresh native samples of the start-of-turn source attribute 2.50% of selected integer self samples to `get_Current` and 2.65% to `PopHeldRoot`; for reference payloads the shares are 2.34% and 2.12%. These percentages are not a sum of removable overhead: thread-static reads and transaction checks still execute after inlining.

FullOpts ARM64 JIT diagnostics provide the direct code-generation evidence:

| Method, `int/int` | Before, bytes | After, bytes |
|---|---:|---:|
| `ThreadHandle.get_Current` | 176 | Inlined; no standalone body emitted in this diagnostic |
| `EnqueueCore` | 472 | 568 |
| `TryDequeue` | 1152 | 1248 |
| `PopHeldRoot` | 140 | Inlined; no standalone body emitted in this diagnostic |
| `TryLockedPop` | 216 | 360 |

Both standalone frames disappear from the final native captures. The hot accessor still reads the **same non-generic `[ThreadStatic]` field on every operation**. Only a first access calls the non-inlined helper, which performs the same allocation, RNG initialization and field assignment. There is no queue-instance cache, cross-thread sharing, or caching across `await`. First use still allocates/seeds, now through a separate helper; steady-state timings deliberately exclude initialization and do not establish an initialization-speed improvement.

Inlining `PopHeldRoot` keeps the same transaction predicates and calls the same buffered/unbuffered mutators. It does not inline or replace the transaction implementation by hand. The comparer whitelist, preflight sifts, journal/snapshots, reference clearing, reservation cleanup, comparison order and publication logic are unchanged. These are JIT hints and a cold-path factoring change, not changes to MultiQueue's sampling, stickiness, heap, buffering or locking policy.

The original reference stripe metric is sensitive to compilation context. The thread accessor is not used by a stripe-only operation, yet accessor-only experiments changed the stripe timing when it followed whole-queue work. This is evidence against attributing all of the old 3.9% regression to extra executed guard instructions. Inlining removes a real dispatcher call, but code placement/register allocation/context effects remain in the observed totals; the exact microarchitectural contribution is not established.

## Experiments rejected

Thirteen configurations were screened, with the retained combination separately rebuilt and confirmed:

- Eager Boolean guards, partly eager guards, and inlining with the partly eager pop guard did not jointly meet the latency/throughput requirement.
- Outlining only the cold pop-transaction call recovered much of the reference stripe result, but did not deliver the consistent whole-queue benefit of the retained accessor change. Forced combinations also traded buffered-only throughput for stripe gains.
- Caching the push-transaction predicate when the insertion buffer changed reduced some stripe timings but lost sparse throughput. That extra derived state and all associated updates were discarded.
- Explicitly inlining the lazy accessor helped. Separating allocation/seeding into a cold helper kept the common read small. Combining it with inlining the original, short-circuiting pop dispatcher gave the strongest latency result while preserving the buffered paths.

The retained production diff changes only `ThreadHandle.cs` and the method annotation on `SubQueue.PopHeldRoot`. All rejected patches and screening runs are preserved rather than mixed into the final tables.

## Measurement protocol

Same Apple M3 / macOS 14.4.1 arm64, .NET SDK 10.0.101 and runtime 10.0.1 environment as the preceding report. The existing `tools/queue-validation` source was unchanged, and all controls were rebuilt with it. Batch modes disable tiering and report the median of 31 batches of 32,768 enqueue/dequeue pairs after eight warmups, with payload allocation outside measurement. Throughput uses normal tiering and three one-second measurements after a discarded warmup. Queue-pair latency and the internal stripe proxy are distinct metrics; the latter is not production lock-hold time.

- **O**: original Git `6800ae0`, before the new exception guarantee.
- **P**: before the preceding profiling optimization; this is the control for the buffered gains and the 3.9% stripe regression.
- **C**: retained source at the start of this investigation, including those buffered gains.
- **F**: final accessor/dispatcher optimization.

Final confirmation uses three O/P/C/F/F/C/P/O isolated blocks (six processes per version), two P/C/F/F/C/P buffered-only blocks, two such full-throughput blocks (four processes per version), and C/F/F/C for the full batch matrix. Every process completed; no outlier was dropped. Some controls vary substantially, so the raw process ranges matter. Medians across processes are descriptive, not confidence intervals or universal performance guarantees.

Native profiling uses the existing driver, eight-second macOS samples at a requested 1 ms interval, and JIT-address rundown collected **after** sampling. Four corrected profiles cover current/final value/reference queue workloads. As in the [preceding profiling report](2026-10-28-api-profile-performance.md), unresolved runtime frames and inline-frame attribution limit percentage interpretation. No timing run overlaps builds, tests or profiling.

## Isolated latency and the reference stripe regression

Custom comparer, population 16, buffer 16, median ns per pair (lower is better; six processes per version):

| Metric | O | P | C | F | F vs C | F vs O |
|---|---:|---:|---:|---:|---:|---:|
| Value queue | 62.835 | 65.565 | 64.825 | 62.365 | -3.79% | -0.75% |
| Reference queue | 70.005 | 71.760 | 71.760 | 70.430 | -1.85% | +0.61% |
| Value stripe proxy | 72.050 | 72.535 | 71.570 | 69.760 | -2.53% | -3.18% |
| Reference stripe proxy | 88.670 | 81.135 | 84.130 | 80.730 | -4.04% | -8.95% |

The fresh controls reproduce the reference stripe regression: C is 3.69% slower than P. F is **4.04% faster than C and 0.50% faster than P**, so that measured regression is recovered. Value queue latency improves **3.79%** versus C and has no remaining regression in the median against O. Reference queue latency improves **1.85%** versus C, leaving **0.61%** versus O. These sub-percent comparisons against O should not be treated as proven universal differences; process ranges overlap.

A separate diagnostic removes whole-queue work and tests each payload in its own process, with the same custom comparer, keys, population, buffer capacity, batch size and warmups. It uses no thread handle or MultiQueue routing. Six processes per version give:

| Stripe-only payload | C, ns | F, ns | F vs C |
|---|---:|---:|---:|
| value | 70.700 | 69.405 | -1.83% |
| reference | 79.120 | 79.495 | +0.47% |

The independent reference result is effectively unchanged within its variation (C: 78.28–80.68 ns; F: 77.52–81.23 ns). It does not substantiate a general 4% reference-stripe speedup. It does support distinguishing recovery of the original context-sensitive regression from a universal stripe cost reduction.

## Buffered throughput preservation

Buffered-only single-thread Mops/s, counting enqueue and dequeue separately (higher is better; four processes per version):

| Population | P | C | F | F vs C | F vs P |
|---:|---:|---:|---:|---:|---:|
| 16 | 41.798 | 42.422 | 42.587 | +0.39% | +1.89% |
| 4096 | 32.401 | 33.508 | 33.912 | +1.20% | +4.66% |

Full throughput matrix, Mops/s (four processes per version):

| Population | Buffer | Threads | P | C | F | F vs C | F vs P |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 16 | 0 | 1 | 44.265 | 43.921 | 45.082 | +2.64% | +1.85% |
| 16 | 0 | 4 | 24.456 | 24.027 | 24.691 | +2.77% | +0.96% |
| 16 | 16 | 1 | 42.932 | 44.096 | 44.511 | +0.94% | +3.68% |
| 16 | 16 | 4 | 22.917 | 23.077 | 23.072 | -0.02% | +0.68% |
| 4096 | 0 | 1 | 31.263 | 31.141 | 31.701 | +1.80% | +1.40% |
| 4096 | 0 | 4 | 36.973 | 36.918 | 37.260 | +0.93% | +0.77% |
| 4096 | 16 | 1 | 32.365 | 34.075 | 34.731 | +1.92% | +7.31% |
| 4096 | 16 | 4 | 30.878 | 31.229 | 31.480 | +0.81% | +1.95% |

Buffered single-thread medians preserve the previous gains: F improves another **0.94% sparse / 1.92% dense** over C in the full matrix, and **0.39% / 1.20%** in the buffered-only sequence. Against P, the full-matrix gains are now **3.68% / 7.31%**. Buffered four-thread medians are essentially level or higher than C. This is not evidence of universal concurrent scaling gains: some control processes varied substantially, and every run is retained. For example, C's full-matrix sparse buffered single-thread results range from 39.982 to 44.872 Mops/s, while F ranges from 44.216 to 44.799.

## Full batch matrix and remaining tradeoffs

Whole-queue pair medians, ns (two processes per version):

| Payload | Comparer | Population | Buffer | C | F | F vs C |
|---|---|---:|---:|---:|---:|---:|
| value | default | 16 | 0 | 62.065 | 59.815 | -3.63% |
| value | default | 16 | 16 | 63.015 | 60.700 | -3.67% |
| value | default | 4096 | 0 | 72.885 | 70.725 | -2.96% |
| value | default | 4096 | 16 | 70.610 | 68.520 | -2.96% |
| value | custom | 16 | 0 | 64.090 | 62.370 | -2.68% |
| value | custom | 16 | 16 | 63.845 | 62.265 | -2.47% |
| value | custom | 4096 | 0 | 109.890 | 107.815 | -1.89% |
| value | custom | 4096 | 16 | 81.140 | 80.195 | -1.16% |
| reference | default | 16 | 0 | 64.410 | 61.840 | -3.99% |
| reference | default | 16 | 16 | 68.995 | 67.165 | -2.65% |
| reference | default | 4096 | 0 | 80.560 | 78.050 | -3.12% |
| reference | default | 4096 | 16 | 75.590 | 73.805 | -2.36% |
| reference | custom | 16 | 0 | 67.050 | 64.320 | -4.07% |
| reference | custom | 16 | 16 | 71.200 | 69.180 | -2.84% |
| reference | custom | 4096 | 0 | 126.885 | 124.345 | -2.00% |
| reference | custom | 4096 | 16 | 94.430 | 92.910 | -1.61% |

All sixteen whole-queue batch medians improve. Not every internal stripe metric improves:

- The full matrix's sparse buffered custom-reference stripe proxy improves from **81.520 to 80.970 ns (−0.67%)**, a smaller change than in the original isolated context.
- A different, **dense unbuffered custom-reference stripe proxy** increases from **82.365 to 85.515 ns (+3.82%)**. Its whole-queue pair latency nevertheless improves from 126.885 to 124.345 ns (−2.00%). This remaining internal-microbenchmark tradeoff is not the buffered reference regression that motivated this turn.
- The independent reference stripe-only diagnostic is +0.47% in the median with overlapping ranges. The accessor optimization cannot accelerate an operation that never uses the accessor; the inlining/compilation context matters.
- Unknown-comparer exception protection still costs preflight/commit sifts, snapshots, journaling and clearing. None was weakened to obtain these results. Storage-growth behavior is unchanged; the new hot path adds no allocation.
- Native AOT and the local CoreCLR correctness checks pass, but performance was measured only on this ARM64 runtime/host, with integer priorities and inexpensive comparers. Other architectures, runtimes, expensive/reference priorities, production latency tails, cold thread initialization and high-core-count scaling remain unmeasured.

## Correctness and evidence

- **1,224 tests pass, no skips:** 143 concurrency (including the exhaustive comparer-failure matrix and thread-churn coverage), 795 main and 286 scheduling. No test expectation or safety assertion was relaxed.
- **72/72 trace hashes match O:** unique and duplicate priorities, including returned identities, heap/buffer state, publication and drain.
- **160/160 rank/delay records match O.** RNG transitions, sampling/stickiness, tie decisions, heap arity, buffer behavior and try-lock resampling are preserved.
- Release solution build: zero warnings/errors. Native AOT smoke passes for reference entries and compact values with default/custom comparers. The previously documented publish-only SourceLink audit workaround remains; no project audit suppression was added.
- The existing public API decisions and internal exact dispatch remain untouched. There is no new shared/per-thread state, profiler or test hook in production.

[Raw controls, native profiles, JIT output, source hashes, rejected experiments, stripe-only diagnostic and verification logs](data/api-latency-performance-2026-10-28/README.md) include reproduction instructions and the small retained patch. The [prior report](2026-10-28-api-profile-performance.md) records the preserved copy/guard optimization.
