# Residual MultiQueue performance investigation

Date: 2026-10-28. Follow-up to the [targeted performance report](2026-10-28-api-targeted-performance.md).

**No additional production optimization is retained.** Fourteen variants were evaluated. The better isolated-latency results came with regressions in another requested workload, or failed to repeat in confirmation. The queue source is byte-for-byte identical to the implementation at the start of this investigation. Comparer exception safety, MultiQueue routing and buffer behavior are unchanged.

The retained changes strengthen verification: exhaustive comparer-failure tests now cover both bounded and unbounded admission, the differential harness has duplicate-priority traces, and `buffered-single` isolates the two buffered single-thread throughput cells.

## What the investigation established

1. **Some apparent residual cost depends on the benchmark context.** With identical queue source, four paired runs of the old and updated harness measured 66.54 versus 65.20 ns for isolated value payloads, and 71.24 versus 70.79 ns for reference payloads. This is not a library speedup. The harness change in that control only added the `buffered-single` filter; the sparse timing loop was unchanged. It shows that a few nanoseconds cannot be attributed solely to the safety mechanism. Code placement/JIT effects and host variability are possible contributors, not proven causes. Final comparisons always compile every version with the same harness.
2. **Rollback transactions are uncommon in the sparse workload.** A diagnostic build counted 19,011 transactional pops in 1,015,808 measured whole-queue pairs (1.87%), and zero transactional pushes. Dense buffered custom-comparer batches counted 40,651 transactional pushes and 1,958 transactional pops. Counters existed only in a temporary diagnostic source copy. These counts are not performance measurements and no instrumentation was added to the library.
3. **Code size alone did not predict a speedup.** Full-optimization arm64 inspection found the safe lock-taking push wrapper at 492 bytes versus 248 originally; the safe pop dispatcher was out of line. Inlining the dispatcher, outlining the unbuffered push or cold transaction call, and moving transaction guards into buffered operations did not produce a convincing improvement across both payload types and throughput modes.
4. **Smaller rollback snapshots did not pay off.** Preserving only the single deletion-buffer root rather than copying its entire storage added a specialized recovery path without a repeatable sparse improvement. It was discarded; the existing journal, snapshots and reference clearing remain untouched.
5. **Removing redundant comparisons reduced work but did not improve the requested workloads together.** Routing and insertion compare the same incoming priority with the deletion-buffer maximum when there is room. Reusing that result passed the exception suite and all algorithm traces, including ties. However, confirmation of the strongest variant worsened reference latency and sparse buffered throughput. Its forced inlining also grew the buffered push body from 380 bytes to 836/904 bytes for value/reference elements. It was discarded.
6. **Separating unbounded enqueue from bounded reservation cleanup also had a tradeoff.** The unbounded path needs no capacity rollback; successful enqueues do not execute the catch body. Factoring out the shared, unchanged resampling loop reduced isolated latency, but hurt default-comparer sparse buffered throughput. The explicit-inlining variant also retained a substantial throughput loss. Neither enqueue refactor is retained.

## Controls and timing method

Same Apple M3 host (8 cores, 16 GiB), macOS 14.4.1 arm64, .NET SDK 10.0.101/runtime 10.0.1. All timed executables compile the same harness without `BIFROST_TEST_HOOKS`:

- **Original (O):** `6800ae0`, before the comparer-exception guarantee.
- **Earlier safe (E):** the version preceding the last targeted optimization, needed to reassess the reported 1–2% buffered slowdown.
- **Current (C):** the source at the start and end of this investigation.

The completed target-confirmation run used O/E/C/K/K/C/E/O per block, where K is the subsequently rejected comparison-reuse candidate. There were three sparse blocks (six processes per version) and two buffered-throughput blocks (four processes per version). Its target results are retained in full. After K was rejected, its runner was stopped during the subsequent throughput matrix; those incomplete matrix records are exploratory only and are not used below.

The retained source was then rebuilt and validated, followed by a separate complete O/E/C/C/E/O run of the full throughput and batch matrices. No builds, tests or other benchmark runs overlapped these confirmation measurements. Tables use the arithmetic mean of process-level medians. Ranges are minima/maxima of those medians, not confidence intervals.

`batch-sparse` and `batch` disable tiering and measure 31 batches of 32,768 pairs after eight warmup batches. `buffered-single` and the full throughput matrix use normal tiering and three one-second samples after a discarded warmup. The batch and throughput modes also differ in key sequence and loop shape, so the differences do not isolate tiering alone. Values and preallocated reference payloads both use integer priorities; custom comparers perform integer comparison.

## Reassessment of the remaining targets

Means of process medians; current ranges show the observed spread. Lower ns/pair is better; higher Mops/s is better.

| Scenario | O | E | C [range] | C vs E | C vs O |
|---|---:|---:|---:|---:|---:|
| Sparse custom, value (ns/pair) | 62.597 | 68.837 | 65.345 [64.240–65.870] | -5.07% | +4.39% |
| Sparse custom, reference (ns/pair) | 69.235 | 73.108 | 71.340 [70.010–71.930] | -2.42% | +3.04% |
| Buffered, population 16, one thread (Mops/s) | 41.791 | 42.257 | 42.221 [42.012–42.297] | -0.08% | +1.03% |
| Buffered, population 4096, one thread (Mops/s) | 32.485 | 32.559 | 32.556 [32.401–32.727] | -0.01% | +0.22% |

The isolated current-version gaps are **4.39% for value payloads and 3.04% for reference payloads versus original**. Buffered throughput is within 0.1% of the earlier safe version in isolation. The full-matrix run below still shows **1.49% and 0.78% deficits versus earlier safe** for sparse/dense buffered single-thread throughput. These context-dependent costs remain; no production speedup from this investigation is claimed.

The original 4–7% and 1–2% numbers are observations from particular runs, not constants of the implementation. The tables distinguish the original unsafe baseline from the earlier safe baseline and preserve the ordering/context differences.

## Why the strongest candidates were rejected

Changes relative to each candidate's contemporaneous current-version control. Negative latency change is better; positive throughput change is better.

| Rejected variant | Sparse value latency | Sparse reference latency | Sparse buffered throughput | Dense buffered throughput |
|---|---:|---:|---:|---:|
| K: reuse comparison + inline insertion | -0.22% | +1.66% | -1.06% | +0.33% |
| N: separate unbounded enqueue + explicit inlining | -1.56% | -0.93% | -4.08% | -0.29% |

K uses six sparse-latency processes and four throughput processes per version; N uses six and two, respectively. These are descriptive comparisons, not significance tests. The plain enqueue refactor M also lost buffered throughput and was rejected.

K passed the safety/algorithm checks but did not improve the requested targets together. The enqueue candidate N used the same stripe mutation and resampling code, but its throughput loss outweighed its small isolated gains. No claim is made that all future optimizations are impossible; these candidates did not justify changing the production implementation.

## Full-matrix cross-check of the retained implementation

Throughput in Mops/s (higher is better).

| Population | Buffer | Threads | O | E | C | C vs E | C vs O |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 16 | 0 | 1 | 42.718 | 44.642 | 44.591 | -0.11% | +4.39% |
| 16 | 0 | 4 | 24.977 | 24.364 | 24.655 | +1.19% | -1.29% |
| 16 | 16 | 1 | 43.377 | 43.819 | 43.169 | -1.49% | -0.48% |
| 16 | 16 | 4 | 23.136 | 23.032 | 22.961 | -0.31% | -0.76% |
| 4096 | 0 | 1 | 31.199 | 31.151 | 31.374 | +0.72% | +0.56% |
| 4096 | 0 | 4 | 37.259 | 37.099 | 37.543 | +1.20% | +0.76% |
| 4096 | 16 | 1 | 33.034 | 32.788 | 32.532 | -0.78% | -1.52% |
| 4096 | 16 | 4 | 31.501 | 31.684 | 31.086 | -1.89% | -1.32% |

Batched pair latency in ns (lower is better).

| Payload | Comparer | Population | Buffer | O | E | C | C vs E | C vs O |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| value | default | 16 | 0 | 61.02 | 62.06 | 61.23 | -1.33% | +0.34% |
| value | default | 16 | 16 | 61.61 | 63.35 | 62.98 | -0.58% | +2.22% |
| value | default | 4096 | 0 | 72.08 | 73.38 | 72.31 | -1.46% | +0.32% |
| value | default | 4096 | 16 | 74.47 | 74.90 | 75.11 | +0.28% | +0.85% |
| value | custom | 16 | 0 | 63.06 | 63.70 | 63.31 | -0.61% | +0.39% |
| value | custom | 16 | 16 | 63.24 | 68.40 | 64.56 | -5.60% | +2.10% |
| value | custom | 4096 | 0 | 105.71 | 109.40 | 109.27 | -0.12% | +3.37% |
| value | custom | 4096 | 16 | 87.34 | 86.56 | 86.39 | -0.19% | -1.09% |
| reference | default | 16 | 0 | 64.16 | 63.61 | 63.45 | -0.24% | -1.10% |
| reference | default | 16 | 16 | 67.97 | 68.42 | 68.62 | +0.28% | +0.96% |
| reference | default | 4096 | 0 | 77.78 | 79.53 | 79.53 | +0.00% | +2.26% |
| reference | default | 4096 | 16 | 75.95 | 75.28 | 76.63 | +1.80% | +0.89% |
| reference | custom | 16 | 0 | 66.03 | 66.55 | 66.56 | +0.03% | +0.80% |
| reference | custom | 16 | 16 | 69.11 | 72.84 | 71.06 | -2.44% | +2.82% |
| reference | custom | 4096 | 0 | 119.72 | 126.13 | 126.45 | +0.25% | +5.63% |
| reference | custom | 4096 | 16 | 93.78 | 93.70 | 94.78 | +1.15% | +1.06% |

## Verification and remaining limits

- **1,218 tests passed:** 137 concurrency, 795 main and 286 scheduling, with no skips. The failure-position matrix now has twelve configurations: buffers 0/1/16, explicit custom comparer or throwing user-defined default priority, and bounded/unbounded capacity. Existing bounded-capacity checks remain intact; unbounded cases additionally verify that no reservation count is introduced and admission continues beyond the refill size.
- **72/72 trace hashes match original:** 36 unique-priority histories and 36 duplicate-priority histories. They include returned element identities, buffer/stripe state, publication and final drain across stripe counts, buffering and stickiness.
- **160/160 rank/delay records match original.**
- Retained-source Release solution build: zero warnings/errors. Native AOT smoke passed with 4,096 buffered reference entries. The previously documented local SourceLink dependency audit workaround (`NuGetAudit=false` on the publish command only) remains; no project audit suppression was introduced.
- Production source was compared byte-for-byte with the start-of-investigation snapshot. All candidate production changes, including comparison reuse, forced inlining and enqueue factoring, were discarded.

Remaining overhead is not eliminated. Unknown comparers still require preflight/commit sifts and rollback for compound mutations. This investigation does not establish that a measured nanosecond gap is entirely the cost of that guarantee, nor that a small throughput difference will hold on another machine or workload. High-core-count servers, other priority types, expensive comparers and end-to-end dispatch remain outside this local harness's claims.

[Raw controls, complete retained-version matrices, rejected-candidate patches, diagnostics and verification logs](data/api-residual-performance-2026-10-28/) are preserved with reproduction instructions. One exploratory overlap between the end of the I trial and the start of the J trial is explicitly marked in the data index; those measurements were not used for the confirmation tables or the retention decision.
