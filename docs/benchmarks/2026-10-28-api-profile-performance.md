# MultiQueue profiling and performance follow-up — 2026-10-28

This investigation retains two small changes: classify known non-throwing comparers before reading buffered-transaction trigger fields, and shift deletion-buffer entries with a forward scalar copy when the complete entry contains no references and is at most eight bytes. Larger entries and reference-containing entries retain `Span.CopyTo`. The transaction boundary, snapshots, journal, preflight sifts, comparer whitelist and comparison order are unchanged.

The buffered single-thread throughput regressions are reduced. The isolated custom-comparer latency gap is **not convincingly eliminated**. The reference-only stripe proxy also has a measurable tradeoff, described below. No new public API or MultiQueue policy is introduced.

## What profiling established

Measurements used the same Apple M3, macOS 14.4.1 arm64, .NET SDK 10.0.101 / runtime 10.0.1 host as the preceding investigation. The new `tools/queue-profile` driver extends the existing validation workloads for sampling; **its timings are not benchmark results**. The timing harness itself was not changed.

Eight-second macOS `sample` captures were collected at a requested 1 ms interval, then `dotnet-trace` 10.0.750501 collected JIT method rundown from the still-running process. The mapping collector ran **after** native sampling. Method address/size ranges map sampled PCs back to managed methods. Batch profiles disable tiering; throughput profiles use normal tiering and the full matrix's preceding workload order. Only stacks beneath `QueueBatch` or `ThroughputTarget` are counted, excluding the stripe control and warmup.

This approach matters because [EventPipe does not resolve native or kernel stacks](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventpipe). Managed-only samples concentrated at buffer-copy transitions are not an adequate CPU-cost breakdown. Native samples, JIT assembly and unprofiled controls were used together. These are sample shares, not cycle counts or statistically independent timing estimates; inline operations appear under their callers, and roughly 7–10% of the selected isolated samples remain unnamed runtime/JIT frames.

An initial profiling-driver constructor call selected the bounded overload. All profiles from that setup were discarded, including a dense run that failed during prefill. Named `subQueueCount` and `boundedCapacity` arguments corrected it; every retained profile exited successfully. The unchanged timing harness already used named arguments, so its measurements were unaffected.

### Isolated custom-comparer latency

The remaining work is spread across dispatch/guards and occasional compound-operation recovery, rather than one large copy hotspot:

- In the pre-change integer profile, the separate `PopHeldRoot` dispatcher accounts for 1.45% of selected self samples; it accounts for 1.86% with reference payloads. The original has no separately emitted dispatcher in these FullOpts compilations. JIT output shows original `TryLockedPop` at 284 bytes versus 216 bytes plus a separate 140-byte dispatcher in the safe implementation. This is concrete extra dispatch work, but sample percentages do not measure its exact contribution to the latency delta.
- `BufferedTransaction` accounts for 1.24% of inclusive selected integer samples. That includes useful refill work as well as recovery preparation, so it must **not** all be counted as incremental safety overhead. The [earlier instrumented diagnostic](2026-10-28-api-residual-performance.md) found that only about 1.87% of sparse pairs enter this transaction; this investigation does not put instrumentation into the measured queue.
- Unknown-comparer sifts retain preflight/commit work and journal writes. Reference transactions additionally copy GC-tracked snapshots and clear journal references. Removing these operations would weaken exception safety; they remain.
- Buffer copies are a small portion of the sparse path. Locks, two-choice routing and thread-local access dominate, and their policy is unchanged. Prior forced-inlining experiments already traded isolated gains for throughput losses; they were not reintroduced.

The final isolated results below therefore remain a tradeoff for the stronger guarantee plus runtime/code-layout effects. The data does not establish an exact percentage attributable solely to exception safety.

### Buffered throughput with the default integer comparer

These operations do not enter comparer rollback. The previous count-first guards nevertheless read transaction-trigger fields before discovering that the comparer is non-throwing. JIT assembly confirms those field loads and conditional branches. Checking the comparer first removes those reads on this path. This reverses the guard-order choice from the earlier sparse-focused tuning while retaining the comparison-free-refill exemption.

Dense operation has an additional, independent optimization opportunity: small overlapping `Span.CopyTo` calls enter the runtime/native memmove path. Before the change, `MemmoveInternal` and its descendants account for **25.12%** of selected dense samples (earlier safe control: 25.02%); after the change they account for **11.27%**. Inclusive buffered-pop share falls from **21.91% to 17.13%**. Much of the removed native-copy work becomes managed loop work, so the 14-point change in native-copy share is not a 14% throughput claim.

The retained forward loop is overlap-correct and makes no comparer calls. Its type and size guards avoid applying a small-entry optimization to large structs or GC references. Native AOT and CoreCLR both exercise this specialization. The integer and reference FullOpts buffered-pop instructions match the preceding candidate after address relocations are normalized; final-source timing was still repeated independently.

## Experiments and measurement discipline

Five exploratory candidates were tested against the start-of-turn source: scalar insertion and removal loops; one-entry copy special cases; reference-free removal loops; comparer-first guards; and the combination. Broad scalar loops improved dense integer throughput but severely hurt the reference stripe proxy. One-entry special cases did not improve the targets. The retained version narrows the combination to entries of at most eight bytes.

All timing processes ran sequentially, without builds, tests or profilers alongside them. Batch modes use 31 batches of 32,768 pairs after eight warmups, with tiering disabled and payloads allocated before measurement. Throughput uses normal tiering and the median of three one-second samples after a discarded warmup. Reported medians across processes are descriptive, not confidence intervals. Allocation behavior remains unchanged: sparse cells report zero; dense cells can still grow backing storage.

Versions:

- **O**: original Git `6800ae0`, before the new exception guarantee.
- **E**: earlier safe implementation, before the preceding targeted optimizations.
- **C**: retained source at the start of this investigation.
- **F**: final retained source, including the eight-byte cutoff.

The exploratory combination received a complete O/E/C/N/N/C/E/O sequence: three isolated blocks, one buffered-only block, one full throughput matrix and one full batch matrix. Final F was then rebuilt and tested in three O/C/F/F/C/O isolated blocks, E/C/F/F/C/E buffered-only and full-throughput sequences, and a C/F/F/C full-batch sequence. Both complete sequences, all exploratory results, process ranges and patches are retained. No partial run is used in the tables.

## Final-source results

Isolated custom-comparer, population-16, buffer-16 **queue pair latency**, ns (lower is better; six processes per version):

| Payload | O | C | F | F vs C | F vs O |
|---|---:|---:|---:|---:|---:|
| value | 62.415 | 65.180 | 64.985 | -0.30% | +4.12% |
| reference | 69.895 | 71.560 | 71.415 | -0.20% | +2.17% |

The final isolated changes are only about −0.3% and −0.2%, with overlapping process ranges. They do not establish a repeatable latency improvement. The exploratory combination had shown a 1.67% integer improvement, but that result was not reproduced at the same magnitude. The unchanged C integer median moved from 65.955 ns to 65.180 ns between the two complete confirmations. The remaining final gaps versus O are about **4.1% for value payloads and 2.2% for references**.

Buffered-only, single-thread throughput, Mops/s (higher is better; two processes per version):

| Population | E | C | F | F vs C | F vs E |
|---:|---:|---:|---:|---:|---:|
| 16 | 41.526 | 41.700 | 42.228 | +1.27% | +1.69% |
| 4096 | 32.212 | 32.639 | 33.371 | +2.24% | +3.60% |

Full throughput matrix, Mops/s, counting enqueue and dequeue separately (two processes per version):

| Population | Buffer | Threads | E | C | F | F vs C | F vs E |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 16 | 0 | 1 | 44.663 | 44.735 | 44.703 | -0.07% | +0.09% |
| 16 | 0 | 4 | 24.356 | 24.522 | 24.454 | -0.28% | +0.40% |
| 16 | 16 | 1 | 43.953 | 43.429 | 44.569 | +2.63% | +1.40% |
| 16 | 16 | 4 | 23.001 | 22.938 | 23.035 | +0.42% | +0.15% |
| 4096 | 0 | 1 | 31.224 | 31.343 | 31.316 | -0.09% | +0.29% |
| 4096 | 0 | 4 | 37.079 | 36.872 | 36.897 | +0.07% | -0.49% |
| 4096 | 16 | 1 | 32.659 | 32.466 | 34.007 | +4.75% | +4.13% |
| 4096 | 16 | 4 | 31.310 | 31.233 | 31.061 | -0.55% | -0.79% |

The targeted buffered single-thread cells improve **2.63% sparse / 4.75% dense** in the full matrix and **1.27% / 2.24%** when isolated. In the full matrix they are now ahead of E by 1.40% / 4.13%. The separate dense four-thread buffered cell is 0.55% below C and 0.80% below E in this final sequence; its process ranges overlap C. The exploratory confirmation had the opposite sign, so this does not establish a stable concurrent throughput change.

Full batch matrix, queue-pair median ns (two processes per version):

| Payload | Comparer | Population | Buffer | C | F | F vs C |
|---|---|---:|---:|---:|---:|---:|
| value | default | 16 | 0 | 61.680 | 62.460 | +1.26% |
| value | default | 16 | 16 | 62.945 | 63.015 | +0.11% |
| value | default | 4096 | 0 | 72.530 | 72.820 | +0.40% |
| value | default | 4096 | 16 | 75.755 | 71.555 | -5.54% |
| value | custom | 16 | 0 | 63.785 | 64.190 | +0.63% |
| value | custom | 16 | 16 | 64.440 | 63.920 | -0.81% |
| value | custom | 4096 | 0 | 110.295 | 109.420 | -0.79% |
| value | custom | 4096 | 16 | 86.730 | 81.355 | -6.20% |
| reference | default | 16 | 0 | 64.100 | 64.860 | +1.19% |
| reference | default | 16 | 16 | 69.145 | 69.545 | +0.58% |
| reference | default | 4096 | 0 | 80.095 | 80.970 | +1.09% |
| reference | default | 4096 | 16 | 77.240 | 75.825 | -1.83% |
| reference | custom | 16 | 0 | 66.770 | 67.085 | +0.47% |
| reference | custom | 16 | 16 | 71.515 | 71.320 | -0.27% |
| reference | custom | 4096 | 0 | 125.735 | 126.620 | +0.70% |
| reference | custom | 4096 | 16 | 95.070 | 93.950 | -1.18% |

## Remaining tradeoffs

- The isolated reference stripe proxy rises from **81.240 to 84.405 ns (+3.90%)**. It remains faster than O's 89.005 ns, but regresses against C. The same reference/custom/population-16/buffer-16 stripe cell in the full batch matrix moves the other way: **83.430 to 80.435 ns (−3.59%)**. This context sensitivity and the reordered guards prevent a claim of a general reference-path speedup. Reference buffered-pop assembly itself retains the bulk-copy implementation.
- Several unbuffered batch cells are roughly 0.4–1.3% slower, despite no new work in the unbuffered algorithm; normal-tiered unbuffered throughput stays within about 0.3% of C. These small code-generation/context effects are recorded rather than hidden by the buffered improvements. This change is not a win in every cell.
- Strong exception safety still costs preflight/commit work, transaction dispatch, snapshots and reference-journal clearing for unknown comparers. The new loop only replaces a copy; it does not remove that protection or promise zero isolated overhead.
- The eight-byte cutoff deliberately leaves larger value entries on the bulk-copy path. Other architectures/runtimes, reference or expensive priorities, high-core-count contention, production latency tails and end-to-end dispatch were not validated by these local microbenchmarks. The stripe proxy is not a production lock-hold distribution.

## Correctness and reproducibility

- **1,224 tests passed, no skips:** 143 concurrency, 795 main, 286 scheduling. Six additional exhaustive failure-position configurations cover compact integer entries with buffers 0/1/16 and bounded/unbounded queues. The existing twelve reference-entry/default-or-custom-priority configurations remain. They verify contents, counts/reservations, publication version, buffer counts, recovery drain and subsequent admission after every injected comparer failure.
- **72/72 original trace hashes match:** 36 unique-priority and 36 duplicate-priority histories, including identities, heap/buffer layout, publication and final drain.
- **160/160 original rank/delay records match.** Sampling, stickiness, try-lock resampling, two-choice selection, tie decisions, heap arity and flush/refill behavior are unchanged.
- Release solution build: zero warnings/errors. Native AOT smoke passes for 4,096 reference entries plus 4,096 compact value entries with each of default and custom comparers. The existing publish-only `NuGetAudit=false` workaround for the previously documented SourceLink dependency remains; no project audit setting was changed.
- Runtime behavior changes only in `SubQueue.cs`; `SubQueueBuffer.cs` receives a documentation correction for scalar shifts. Test, smoke, profiling-tool and documentation changes accompany it. No public locking queue was restored; internal exact dispatch is unaffected.

[Raw profiles, JIT output, all timing runs, rejected patches, source hashes and verification logs](data/api-profile-performance-2026-10-28/README.md) provide the evidence and reconstruction steps. [The profiling driver](../../tools/queue-profile/README.md) and [unchanged timing harness](../../tools/queue-validation/README.md) have separate purposes. Production contains no profiler, new RNG control or benchmark instrumentation.
