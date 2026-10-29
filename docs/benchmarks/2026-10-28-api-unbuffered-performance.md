# MultiQueue unbuffered reference-stripe investigation — 2026-10-28

**No additional queue optimization was retained.** Fourteen candidates were investigated. Initial improvements either failed repeated confirmation or traded away queue latency. The production concurrency sources are byte-for-byte identical to the queue code published in PR #69 at `6fb7920`; comparer exception safety, the MultiQueue algorithm, and the existing latency/buffering optimizations are preserved.

The previously reported **3.82% dense unbuffered custom-reference stripe regression** is smaller in fresh full-matrix measurements: **1.16%** (83.780 → 84.755 ns per pair, six processes per version). This is a new measurement of unchanged code, **not an optimization or a claim that the regression was fixed**. An independent stripe-only diagnostic reverses the result: the current version is **2.20% faster** (83.545 → 81.710 ns). The remaining tradeoff is sensitive to compilation/workload context; there is no consistent 3.8% increase in the cost of the isolated stripe operation.

## Controls and protocol

Same Apple M3 / macOS 14.4.1 arm64, SDK 10.0.101 / runtime 10.0.1 host as the [previous report](2026-10-28-api-latency-performance.md). The existing `tools/queue-validation` driver is unchanged. Batch modes disable tiering and use 31 batches of 32,768 pairs after eight warmups, with payload allocation outside timing. Throughput uses normal tiering and the existing full eight-cell sequence, with three one-second measurements after a discarded warmup per cell. No benchmark overlaps local builds, tests, or another benchmark.

| Name here | Raw-data name | Source |
|---|---|---|
| O | `original` | Git `6800ae0`, before the comparer guarantee |
| P | `preprofile` | Before the preceding compact-copy/guard improvements |
| I | `old` | Before accessor/dispatcher inlining, with the copy/guard gains |
| C | `current` | PR #69 queue implementation, including all retained gains |
| L | `comparer-local` | Rejected candidate retaining the readonly comparer in a local during downward preflight |

The main confirmation sequence was three I/C/L/L/C/I full-matrix blocks, then three C/L/L/C sparse blocks and three I/C/L/L/C/I stripe-only blocks. Once L failed those comparisons, its planned throughput campaign was stopped; six completed buffered-only processes (three per version) are retained as ancillary data, and are not used to claim a throughput win. Final preservation uses three O/C/C/O sparse blocks and two P/C/C/P full-throughput blocks. Screening and confirmation results remain separate. No outlier from these completed sequences was dropped.

The stripe-only diagnostic is a separate program: only reference payloads, population 4096, buffer capacity 0, the same custom comparer, keys, batch size and warmup count. It does not construct or operate the whole queue and never reads `ThreadHandle.Current`. It complements, rather than replaces, the original full-matrix proxy.

## Target results and code-generation evidence

Custom comparer, reference payload, population 4096, buffer 0; ns per enqueue/dequeue pair:

| Context | I median (range) | C median (range) | C vs I |
|---|---:|---:|---:|
| Full-matrix stripe proxy | 83.780 (82.11–85.28) | 84.755 (83.06–88.73) | +1.16% |
| Independent stripe-only program | 83.545 (82.91–84.17) | 81.710 (80.65–82.83) | −2.20% |
| Full-matrix whole queue | 125.585 (125.08–126.30) | 125.820 (124.46–126.61) | +0.19% |

Six processes contribute to each median. Ranges overlap; descriptive medians are not confidence intervals. The previous full-matrix whole-queue improvement of 2% is also not consistently reproduced by this particular cell. The fresh isolated-latency and buffered-throughput controls below are the evidence for preserving the broader gains.

The earlier dispatcher inlining changes reference `TryLockedPop` from 216 to 360 bytes and adds an `x21` save/restore on ARM64. C still contains this shape. Its unbuffered branch jumps past the buffered blocks; its heap preflight, commit, publication, clearing and finally-based unlock remain the same. Removing the inline annotation recovers some full-matrix stripe timings but gives up reference buffered latency. Keeping the accessor change alone also changes a stripe proxy that never calls the accessor, showing why the proxy cannot be attributed solely to executed thread-local reads.

Fresh JIT dumps explain why seemingly cheaper variants were not free: the comparer-local candidate replaces repeated field loads with a retained register, but grows `MoveDownCustomComparer<reference,int>` from 1432 to 1444 bytes and its fixed frame from 128 to 144 bytes, spilling the saved starting index. An explicit result local in `TryLockedPop` emits the same 360-byte shape. Broader inlining and splitting the sift introduce different call/frame tradeoffs. These observations support a code-generation/context explanation, but **do not establish an exact cache, branch-predictor or instruction-level cause** for each nanosecond. Hardware performance counters were not collected in this follow-up.

## Rejected candidates

Fourteen configurations, with patches and all completed screening outputs in the evidence bundle:

- Accessor-only inlining, removing forced pop-dispatcher inlining: lower target stripe samples, worse buffered reference latency.
- Forced inline or no-inline on the unbuffered pop helper; a direct unbuffered branch in the locked wrapper; outlining the cold transaction call: no consistent combined improvement. The inline helper's initial stripe gain came with queue-latency losses and did not justify retention.
- Inverting the buffered/unbuffered branch, forcing heap-pop inlining, and moving the wrapper result outside `try/finally`: no robust target gain; heap-pop inlining worsened dense reference queue latency.
- A comparer local, with and without unbuffered-helper inlining: the local-only candidate passed initial screening but failed six-process confirmation (target stripe **85.120 vs C 84.755 ns**; sparse isolated value/reference queue **62.695/70.675 vs C 62.305/70.225 ns**). Its separate stripe-only median was effectively unchanged, **81.775 vs 81.710 ns**.
- Specializing the preflight sift to its only caller's root index, with and without the comparer local: increased dense queue and stripe latency.
- Outlining the already-compared descent commit for the custom path alone or both comparer paths: increased dense reference queue latency by roughly 7% in screening.

None changes production. In particular, no comparer whitelist was broadened, preflight or root-publication comparison removed, transaction guard weakened, rollback storage omitted, reference clear skipped, or cached queue state introduced.

## Preserved latency and buffered throughput

Sparse isolated custom-comparer queue, population 16, buffer 16; six processes per version:

| Payload | O, ns/pair | C, ns/pair | C vs O |
|---|---:|---:|---:|
| value | 62.200 | 61.890 | -0.50% |
| reference | 69.450 | 69.860 | +0.59% |

The value gap remains recovered. The reference gap is **0.59%**, consistent with the earlier 0.61%, with overlapping process ranges. No further latency improvement is claimed.

Full throughput matrix, Mops/s (enqueue and dequeue counted separately); four processes per version:

| Population | Buffer | Threads | P | C | C vs P |
|---:|---:|---:|---:|---:|---:|
| 16 | 0 | 1 | 44.335 | 44.870 | +1.21% |
| 16 | 0 | 4 | 24.681 | 24.551 | -0.53% |
| 16 | 16 | 1 | 42.737 | 44.569 | +4.29% |
| 16 | 16 | 4 | 23.066 | 23.081 | +0.07% |
| 4096 | 0 | 1 | 31.377 | 31.546 | +0.54% |
| 4096 | 0 | 4 | 37.070 | 37.112 | +0.11% |
| 4096 | 16 | 1 | 32.383 | 34.830 | +7.56% |
| 4096 | 16 | 4 | 31.222 | 31.899 | +2.17% |

Buffered single-thread gains over P remain **4.29% sparse / 7.56% dense**. Buffered four-thread medians are level or higher. The sparse unbuffered four-thread median is 0.53% lower, with overlapping ranges; these local results do not establish universal scaling or tail-latency improvements. The separate unbuffered stripe-proxy gap remains a documented tradeoff, not a reason to sacrifice the measured buffered gains.

## Verification and PR build fix

- Release solution build: **0 warnings, 0 errors**. All **1,224 tests** pass (143 concurrency, 795 main, 286 scheduling), with no skips or relaxed assertions.
- All **72 unique/tied-priority trace hashes** and **160 rank/delay records** match original Git `6800ae0`. Source hashes also verify that every production concurrency `.cs` file matches the investigation's starting queue implementation.
- Native AOT publish and smoke pass for reference and compact value entries with default/custom comparers. Package auditing stays enabled; the earlier `NuGetAudit=false` publish workaround is no longer needed.
- PR #69's first CI attempt stopped during restore because existing SourceLink 8.0.0 brought in vulnerable `Microsoft.Build.Tasks.Git` 8.0.0. Updated the private build dependency `Microsoft.SourceLink.GitHub` to **10.0.403**, which resolves Tasks.Git/Common to 10.0.403. This follows the [official advisory](https://github.com/dotnet/sourcelink/security/advisories/GHSA-23fw-v26w-5fgq) and [published package dependencies](https://www.nuget.org/packages/Microsoft.SourceLink.GitHub/10.0.403); it does not change queue runtime dependencies or suppress audit checks.
- Performance coverage remains this ARM64 host/runtime, integer priorities and inexpensive comparers. Other JIT/runtime versions, architectures, reference priorities, expensive comparers, cold initialization and production tail latency remain unmeasured.

[Evidence and reproduction instructions](data/api-unbuffered-performance-2026-10-28/README.md) include raw measurements, rejected patches, JIT diagnostics, exact source hashes and verification output. The PR retains its original API changes and exact internal priority dispatch.
