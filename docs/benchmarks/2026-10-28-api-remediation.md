# Priority API remediation and MultiQueue validation

> Performance follow-up: the [optimized implementation and new three-way measurements](2026-10-28-api-performance-followup.md) supersede the initial performance results below. The original measurements remain as the record of the first safe implementation.

Date: 2026-10-28. Baseline: `6800ae0`. This records the breaking implementation of the [review](https://github.com/lvlup-sw/bifrost/blob/86dcd919696214aa1d3c0541b6070b392bebffc6/exa-results/bifrost-api-review-2026-10-27/review.md) and [revised plan](https://github.com/lvlup-sw/bifrost/blob/86dcd919696214aa1d3c0541b6070b392bebffc6/exa-results/bifrost-api-review-2026-10-27/implementation-amendment.md). See the [consumer migration](../migrations/2026-10-priority-api.md) for the new public contracts.

## Findings and disposition

The original review classified its concrete findings as P1/P2. The implementation also addresses critical lifecycle races discovered during the work; no separate reproduced P0 was listed in that review.

| Finding / priority | Resolution | Evidence |
|---|---|---|
| Best-observed scans advertised as exact, P1 | Rename to `TryPeekBestObserved` and `TryDequeueBestObserved`; document skipped unknown tops, false under contention and non-atomic emptiness. | Deterministic unknown-top and held-stripe regressions; existing inspection tests migrated. |
| Comparer failure corrupts heap/reservations, P1 | Preflight potentially throwing sifts; stripe-local reusable rollback journal for compound buffer operations; reserve capacity with an overflow-safe CAS. | Inject every comparison failure position through ascending insertion, descending eviction cascades and drain at buffer sizes 0/1/16. Verify entries, counts, publication version, buffer counts, ordered recovery, clear and bounded refill. |
| Work-type DI configuration shared, P1 | Named work/DLQ options and keyed autoscaling options, metrics, registries, coordinators and engines. | Two work types retain distinct capacity, ordering, DLQ configuration and autoscaling instances; producer interface resolves the final decorator chain. |
| Unsupported starvation/progress guarantees, P1 | Describe bounded priority offsets, actual blocking/retry behavior, and no deterministic rank or waiting-time guarantee. | API XML, README, background and historical-document notices updated; empirical thresholds retained. |
| Model formula presented as implementation theorem, P2 | Separate the idealized with-replacement deletion-only model from distinct sampling, mixed traffic, stickiness and fallback. | Research applicability corrected; measured rank and per-element delay recorded independently below. |
| Weak copy attributed to ConcurrentQueue, P2 | Document unordered, non-atomic copies across stripes, and O(items) enumeration allocation. | Correct Microsoft precedent linked in migration; existing collection tests retained. |
| Shutdown semantics/resources, P1 and critical lifecycle safety | `Complete`/`Completion`, drain versus abort, all dynamically registered workers tracked, cancellation of waits separated from cancellation of work, disposal waits for actual exit. Host drains then aborts at its deadline. | Dynamic-worker drain/disposal, host deadline, in-flight producer shutdown, repeated completion and throwing cancellation-callback regressions. |
| Rejection reason lost, P2 | Result-bearing synchronous admission; preserve capacity/watermark/shutdown decisions through bindings and decorators. | Exact/relaxed reason tests and no-DLQ-on-shutdown regression. |
| Implicit ordering/backpressure, P2 API follow-up | Explicit Fifo/PriorityExact/PriorityRelaxed and Wait/Reject; reject unsupported priority waiting at construction. | Strategy/full-mode validation and migrated dispatch acceptance tests. |
| Ambiguous construction/diagnostics, P2 API follow-up | Options factory with nullable hard Capacity, explicit stripe/stickiness/buffer settings and resolved read-only properties. Remove public integer tuning constructors and capacity sentinel. | Existing configuration tests and all consumers compile; invalid options use existing core validation. |
| Broad producer API, aliases/classification/result default, P2 API follow-up | `IWorkWriter<T>`, canonical Enqueue methods, nullable class override, common correlation options, explicit Uninitialized outcome. | Classification/default-result, decorator, correlation and producer-registration tests. |
| Public LockingPriorityQueue removal | Remove generic public wrapper. Internal exact binding owns a BCL heap; one lock protects heap access, admission, count and completion. Keep independent benchmark-only wrapper. | Exact ordering, capacity, wake-up/completion and contention contract tests; actual exact dispatch through Native AOT scheduler. |

Exact key selection is retained; equal-key FIFO order is not promised. The removed wrapper's arbitrary-comparer API is not recreated internally: the exact binding uses fixed `long` keys and the default comparer. The MultiQueue heap is not extracted into a shared heap implementation.

Optional API expansion (atomic global snapshots, bulk/compound mutations, arbitrary removal, BlockingCollection integration, and a new fairness mechanism) remains outside this change. Each needs a separate atomicity/ownership contract. Removing the public locking wrapper supersedes proposals to expand that wrapper's surface.

## Algorithm preservation

The baseline source copy was byte-checked against `6800ae0`. `ThreadHandle`, two-distinct-choice sampling, routing/retry logic, sticky periods/resets, default sizing, occupancy fallback, verification scanning and the seqlock publication protocol remain unchanged. Inspection renames add no global synchronization.

Unknown comparers compare sift paths before writes. Buffered compound operations record overwritten heap slots and small inline-buffer state in a stripe-owned reusable journal; rollback does not call the comparer. No heap-wide copy, global journal, global lock or new shared atomic is added to unbounded queue operations. Built-in numeric default comparers retain single-pass sifts. The bounded work adapter's producer/completion handshake is separate from the MultiQueue algorithm.

The [reproducible harness](../../tools/queue-validation/README.md) found:

- **36/36 identical trace hashes**, with default and custom comparers, 1/4/16 stripes, stickiness 1/4 and buffer capacity 0/1/16. Traces include element selections and each operation's stripe counts, top observations, buffer counts and publication versions, plus resident layout and final drain.
- **160/160 identical quality records**: mixed histories and drains across populations 64/4096, stripes 4/16, stickiness 1/4, buffers 0/16 and five seeds. Rank mean/p99/max and per-element delay mean/p99/max match exactly.
- Existing equal-priority conservation, occupancy, sparse routing, stickiness, buffer invariants, publication and thread-churn/stress tests pass. No empirical quality threshold was relaxed.

These sequential differential histories establish behavior for the sampled histories. They are not a proof for every schedule and cannot establish identical concurrent rank distributions: even local changes to lock duration can change interleavings.

## Local performance

Host: Apple M3, 8 physical/logical CPUs, 16 GiB RAM; macOS 14.4.1 arm64; .NET SDK 10.0.101/runtime 10.0.1. Test hooks were disabled in both harness builds. Measurements ran without concurrent builds or tests, in baseline/after/after/baseline order. Other host activity and heterogeneous cores remain uncontrolled.

### Throughput

Each cell is the average of two process-level medians. Each median uses three one-second runs after discarding a warmup run; every run also warms each worker with 10,000 enqueue/dequeue pairs. Throughput counts both operations. Priorities use the default int comparer; stripe count is fixed at 32. These descriptive samples do not provide statistical confidence intervals.

| Population | Buffer | Threads | Before Mops/s | After Mops/s | Change |
|---:|---:|---:|---:|---:|---:|
| 16 | 0 | 1 | 44.553 | 43.677 | -2.0% |
| 16 | 0 | 4 | 24.927 | 24.409 | -2.1% |
| 16 | 16 | 1 | 43.531 | 41.135 | -5.5% |
| 16 | 16 | 4 | 23.120 | 23.145 | +0.1% |
| 4096 | 0 | 1 | 32.033 | 31.914 | -0.4% |
| 4096 | 0 | 4 | 37.657 | 37.733 | +0.2% |
| 4096 | 16 | 1 | 33.097 | 32.090 | -3.0% |
| 4096 | 16 | 4 | 31.738 | 31.611 | -0.4% |

The default unbuffered single-thread dense workload is within 0.4% of baseline, and four-thread workloads range from -2.1% to +0.2%. The repeatable single-thread buffered reduction reaches 5.5%; it must not be represented as zero overhead. Early measurements with two-pass sifts for numeric priorities showed larger regressions, which motivated retaining their original single-pass sift path. The remaining safety branches and pre-mutation decisions still change generated code and critical-section work.

### Latency, critical-section work and allocation

Latency runs disable tiered compilation (`DOTNET_TieredCompilation=0`) to avoid compilation transitions during sub-millisecond samples. Reported values average two process runs; raw p50/p99 are included in the data. Stopwatch quantization is visible (roughly 42 ns), so tiny differences and percentile steps should not be overinterpreted.

| Comparer | Population | Buffer | Pair mean ns before → after | Held-pair proxy ns before → after |
|---|---:|---:|---:|---:|
| Default | 16 | 0 | 59.3 → 60.6 | 24.9 → 28.7 |
| Default | 16 | 16 | 58.4 → 61.2 | 38.1 → 39.6 |
| Default | 4096 | 0 | 69.2 → 70.4 | 41.8 → 45.8 |
| Default | 4096 | 16 | 70.8 → 71.2 | 39.2 → 40.5 |
| Custom | 16 | 0 | 60.1 → 62.2 | 41.4 → 51.9 |
| Custom | 16 | 16 | 56.0 → 59.8 | 57.2 → 58.2 |
| Custom | 4096 | 0 | 99.5 → 113.0 | 91.9 → 107.3 |
| Custom | 4096 | 16 | 79.5 → 83.8 | 53.0 → 54.0 |

A pair is one enqueue plus one dequeue. The held-pair proxy excludes outer lock acquisition, sampling and contention; it includes a reentrant push lock and held-root pop on one stripe. It is a critical-section microbenchmark, **not** a production lock-hold profile. Custom comparer means a nonthrowing delegate with integer ordering, routed through the arbitrary-comparer safety path.

The dense unbuffered custom-comparer pair increased about 14% (99.5 to 113.0 ns); its held-pair proxy increased about 17%. This is a measured cost of preserving state across arbitrary comparer exceptions, not a claim of unchanged performance. Buffered custom-comparer pair latency increased about 5% in the dense case.

Most latency cells allocate zero after warmup. The dense buffered custom-comparer cell allocates 24,720 bytes over 100,000 pairs as retained storage grows; neither implementation is promised allocation-free while backing storage grows. Throughput measurements show zero bytes in sparse cases and small retained-storage growth in dense cases. The new journal is reusable, grows only to cover required mutation history, and clears references after use; no fresh transaction object or heap clone is allocated per operation. These finite runs do not prove zero allocation for every workload.

**Release gate:** rerun the existing server throughput/quality/soak benchmarks on suitable high-core hardware, including custom-comparer and reference-payload workloads, and profile actual contended lock-hold time. This eight-core local run does not validate the earlier 64-thread server scaling claims. Functional/algorithm checks pass; performance preservation is qualified by the measured overhead above and this unperformed server check.

## Build and runtime checks

- Release full-solution build with analyzers: zero warnings/errors.
- Main tests: 795 passed; concurrency: 124 passed including stress; scheduling: 286 passed. No skips.
- Both Native AOT samples published and executed. Concurrency exercised buffered reference-element conservation; scheduling exercised exact priority dispatch through DI and actual scheduled execution.
- `git diff --check` passes.

Native AOT restore/publish required a local `-p:NuGetAudit=false` workaround for the repository's pre-existing SourceLink transitive `Microsoft.Build.Tasks.Git 8.0.0` NU1902 advisory. No project audit suppression was added. These results validate compilation and execution, **not a clean dependency vulnerability audit**; the dependency issue remains separate release work. Native host execution used unsandboxed permissions after sandboxed host startup stalled.

[Raw results and verification logs](data/api-remediation-2026-10-28/) retain both before/after observations. Initial exploratory measurements with concurrent builds or tiering transitions were excluded from these tables.

## Documented conventions

The implementation choices use the reviewed primary sources: Microsoft's [PriorityQueue contract](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.priorityqueue-2), [channel completion and waiting](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels), [named options](https://learn.microsoft.com/en-us/dotnet/core/extensions/options#named-options-support-using-iconfigurenamedoptions), [ConcurrentQueue enumeration](https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentqueue-1.getenumerator), [exception-state guidance](https://learn.microsoft.com/en-us/dotnet/standard/exceptions/best-practices-for-exceptions), and [library compatibility rules](https://learn.microsoft.com/en-us/dotnet/core/compatibility/library-change-rules). The strong comparer guarantee is this library's choice, not a claimed BCL PriorityQueue guarantee.

Research context: [Engineering MultiQueues](https://drops.dagstuhl.de/storage/00lipics/lipics-vol204-esa2021/LIPIcs.ESA.2021.81/LIPIcs.ESA.2021.81.pdf) informs buffering and separate rank/delay measurements; [Walzer and Williams](https://arxiv.org/html/2410.08714) provides the idealized stationary analysis with different sampling assumptions. Sources were discovered and reviewed using the Exa research workflow recorded in the original review.
