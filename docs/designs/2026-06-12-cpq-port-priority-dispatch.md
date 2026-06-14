# Design: CPQ Port + Priority-Aware Work Dispatch

**Feature ID:** `cpq-port-priority-dispatch`
**Related:** [issue #17](https://github.com/lvlup-sw/bifrost/issues/17), [issue #16](https://github.com/lvlup-sw/bifrost/issues/16), `lvlup-sw/DataFerry` (source of the port), `lvlup-sw/basileus` research `2026-06-10-concurrent-priority-queue-applications.md` (F1)
**Date:** 2026-06-12

## Problem Statement

Two problems, one feature:

1. **Head-of-line blocking (#17).** All consumer background work funnels through
   `WorkOrchestrator<TWork>`'s single bounded FIFO channel
   (`src/Bifrost/WorkOrchestrator.cs:66` — `Channel.CreateBounded`, `FullMode = Wait`). In
   basileus, interactive sandbox executions (a user is transitively waiting) share that lane with
   batch maintenance work; a burst of batch work queued ahead of an interactive item adds directly
   to user-visible latency. `System.Threading.Channels` cannot reorder.

2. **The CPQ needs a production home.** `lvlup.DataFerry` v2 is a complete, formally reviewed
   MultiQueue `ConcurrentPriorityQueue<TElement, TPriority>` (~3.1K lines, BCL-only dependencies,
   five dedicated test families, published benchmarks including server-class validation: 0 B/op,
   1.7–17.5× over the lock baseline from 4T up). DataFerry is explicitly a BCL-proposal
   *prototype* — its csproj says so. Its production home was always going to be elsewhere.

## Decisions (settled in ideation, 2026-06-12)

| Decision | Choice |
|---|---|
| Scope | **Full vertical, default-off**: CPQ port + Stage 1 instrumentation + strategy contract with both bindings. FIFO remains the default; the pre-registered evidence trigger (interactive p95 queue-wait > 500ms with batch co-resident) gates *enabling* the priority strategy in consumers, not building it. |
| Placement | **New public `Bifrost.Concurrency` package** (peer of `Bifrost.Resilience`). Namespace migration `lvlup.DataFerry.Concurrency` → `Bifrost.Concurrency`. |
| Integration | **Approach A — queue abstraction.** `IWorkQueue<T>` contract; orchestrator rewritten against it; FIFO binding wraps the existing `Channel`; no-regression benchmark gate on the FIFO path. |
| Backpressure | **Class-aware watermark admission + typed rejection** (breaking change to `EnqueueAsync`, approved). Wait-parity was rejected: producer-waits-at-capacity re-introduces priority inversion at the admission boundary — the queue fills with batch work and an interactive item blocks behind it. Displacement (evict-lowest) is structurally unavailable: MultiQueues are not searchable. |
| Priority key | **Virtual-time (WFQ precedent):** `effectivePriority = enqueueTicks − classBoost(workClass)` as a single `long`. Same-class FIFO; interactive jumps a bounded window ahead; starvation bounded by construction; no decrease-key. |
| DataFerry fate | **Frozen as BCL-proposal showcase** with a README pointer to `Bifrost.Concurrency` as the production home. No sync obligation. |
| #16 boundary | The scheduling tick engine **keeps its exact-min sequential `PriorityQueue`** — relaxed dequeue is semantically wrong for sleep-until-top. The CPQ composes at the orchestrator dispatch layer only. A #16 scheduled tick enqueues at `WorkClass.Batch` (resolves #16 open question Q6). |

## Requirements

### DR-1: Port the CPQ into `Bifrost.Concurrency`

New packable, AOT-declared library containing the MultiQueue `ConcurrentPriorityQueue<TElement,
TPriority>` (13 files: shell, Enqueue/Dequeue/Count/Collection/Inspect partials, SubQueue,
SubQueueHeader, PaddedTopSlot, ThreadHandle, PriorityComparerHelpers, SubQueuePopStatus, debug
view) and the lock-based baseline (ported as `LockingPriorityQueue<TElement, TPriority>`, renamed
from `NaiveConcurrentPriorityQueue` — it ships as a supported binding, not a strawman).

**Acceptance criteria:**
- Public API surface preserved from DataFerry v2 (dual-mode dequeue: relaxed primary
  `TryDequeue`, best-effort strict; opt-in bounding; unordered enumeration; rank-error contract
  documented verbatim).
- All five test families ported to `Bifrost.Tests.Concurrency` (TUnit, assertions awaited):
  RankError, ConservationStress, SeqlockTear, ThreadChurn, EmptySemantics.
- Benchmark suites ported under `Bifrost.Benchmarks/Concurrency/` including the rank-error
  empirical gate.
- `IsAotCompatible=true`, trim-warnings-as-errors, zero analyzer suppressions beyond what
  DataFerry's source already carries; `AllowUnsafeBlocks=true` scoped to this package
  (cache-line padding structs).
- 80% coverage gate maintained (repo standard).
- No reference from `Bifrost.Concurrency` to any other Bifrost package (it is a leaf primitive).

### DR-2: Work classes (Stage 1)

A `WorkClass` enum tag at enqueue: `Interactive`, `Default`, `Batch` (ordered; `Default` is the
unstated middle so existing callers' behavior is explainable without choosing a side).

**Acceptance criteria:**
- `EnqueueAsync(TWork work, WorkClass workClass = WorkClass.Default, CancellationToken ct)` — the
  default parameter keeps existing call sites compiling (the *return type* change in DR-6 is the
  deliberate break).
- The class tag travels in a `WorkEnvelope<TWork>` readonly record struct
  `(TWork Work, WorkClass Class, long EnqueuedAtTicks)` — zero additional allocation; the FIFO
  channel becomes `Channel<WorkEnvelope<TWork>>`.
- An options-level classifier delegate `Func<TWork, WorkClass>?` as an alternative to per-call
  tagging (mutually composable: per-call tag wins when not `Default`).

### DR-3: Queue-wait instrumentation by class (Stage 1)

The missing signal that decides whether Stage 2 is ever enabled.

**Acceptance criteria:**
- Histogram `bifrost.orchestrator.queue_wait` (ms), tagged by `work.class`, emitted in
  `Bifrost.OpenTelemetry` from `EnqueuedAtTicks` at dequeue — measured with the orchestrator's
  `TimeProvider`, not wall-clock arithmetic.
- Counter `bifrost.orchestrator.rejected` tagged by `work.class` (DR-6 outcome).
- Existing queue-depth/duration/worker metrics unchanged.
- Documented evidence recipe: the pre-registered Stage-2 trigger is *interactive-class p95
  queue-wait > 500ms (operator-configurable) while batch-class work is co-resident*.

### DR-4: `IWorkQueue<T>` strategy contract (Approach A)

The orchestrator's queue becomes pluggable. The contract is shaped by the try-based structure +
count-semaphore composition from issue #17:

```csharp
public interface IWorkQueue<T>
{
    bool TryEnqueue(in T item);                    // false = at capacity / watermark-rejected
    ValueTask<bool> WaitToDequeueAsync(CancellationToken ct);  // count-semaphore wake
    bool TryDequeue(out T item);
    int Count { get; }                              // approximate permitted (striped counts)
}
```

**Acceptance criteria:**
- `FifoChannelWorkQueue<T>` (default) wraps the existing bounded `Channel` — current semantics
  byte-for-byte, including `Wait`-mode producer behavior internally.
- Both priority bindings satisfy the same contract: `ConcurrentPriorityWorkQueue<T>` (MultiQueue
  CPQ + `SemaphoreSlim` count + watermarks) and `LockingPriorityWorkQueue<T>`
  (`Lock + PriorityQueue` + same composition).
- Selection via `WorkOrchestratorOptions.DispatchStrategy` / fluent builder; **FIFO is the
  default**; bindings are sealed so the JIT devirtualizes the default path.
- Decorator stack (autoscaling, DLQ, health, OTel) reads depth via `Count` and keeps working
  against all bindings.
- Worker wake-up: producers `TryEnqueue` + `Release`; workers `WaitAsync` + `TryDequeue` with a
  retry loop tolerating relaxed-structure spurious misses.

### DR-5: Priority semantics — virtual-time key + bounded starvation

**Acceptance criteria:**
- Priority key: `long effectivePriority = envelope.EnqueuedAtTicks − Boost(Class)` where
  `Boost(Interactive) = options.InteractiveBoostWindow` (default 30s), `Boost(Default) = 0`,
  `Boost(Batch) = −options.BatchPenaltyWindow` (default 0 — batch is unboosted, not penalized, by
  default). Smaller = sooner.
- Aging is inherent: a batch item older than the boost window outranks fresh interactive work —
  the starvation bound is the window itself, by construction. No decrease-key, no re-scoring;
  relaxed rank error (documented `(5/6)·n` expectation) is acceptable dispatch-ordering noise.
- Acceptance test (issue #17): an interactive item enqueued behind N batch items dispatches next,
  modulo documented relaxation, under multi-producer/multi-consumer load.
- Aging test (issue #17): a batch item is not starved beyond the configured bound under sustained
  interactive load.

### DR-6: Watermark admission + typed rejection (breaking change)

**Acceptance criteria:**
- `EnqueueAsync` returns `ValueTask<EnqueueResult>` across **all** strategies;
  `EnqueueResult { Accepted, Rejected }` with a `RejectionReason` (`CapacityExceeded`,
  `WatermarkExceeded`, `Shutdown`). This is the `System.Threading.RateLimiting`-lease idiom:
  admission outcomes in the type system, not exceptions.
- Per-class watermarks on bounded priority strategies: `Batch` admits while
  `Count < 0.9 × Capacity` (configurable), `Default` while `< 0.95`, `Interactive` to full
  capacity. Sheds lowest class first at admission — WRED/priority-load-shedding precedent —
  with no eviction machinery. Approximate striped counts are acceptable for watermark checks.
- FIFO strategy: preserves Wait-mode internally and returns `Accepted` after the wait completes,
  or `Rejected(Shutdown)` on queue completion. Caller-token cancellation is surfaced as
  `OperationCanceledException`, not a rejected result: cancellation is not an admission outcome, so
  it follows the TAP / `ChannelWriter.WriteAsync` convention (completion ⇒ value, cancellation ⇒
  exception), keeping caller-abort distinguishable from shutdown. (This refines the earlier
  "Rejected(Shutdown) on cancellation" sketch, which conflated the two; the three `RejectionReason`
  values are unchanged.)
- Rejected work routes to the DLQ decorator when present (already-idiomatic path); always counted
  in `bifrost.orchestrator.rejected`.
- Migration note in CHANGELOG: this is the deliberate v0.5.0 breaking change; compiler does the
  finding (`ValueTask` → `ValueTask<EnqueueResult>`).

### DR-7: FIFO no-regression gate

**Acceptance criteria:**
- Benchmark: `WorkOrchestrator` enqueue→dispatch round-trip, FIFO-via-`IWorkQueue` vs the current
  direct-`Channel` implementation (pre-change baseline captured first). Gate: ≤ 5% mean
  regression, no new steady-state allocations on the enqueue path.
- Throughput + allocation tracked in `Bifrost.Benchmarks/Orchestrator/`.

### DR-8: Consumer-shaped soak (the missing maturity-gate item)

DataFerry's published results cover contended micro-benchmarks; the regime Bifrost actually
lives in — 1–8 workers, seconds-long work items, low queue contention — is exactly where the
lock baseline is competitive ("loses only where the baseline's critical section is too cheap to
convoy").

**Acceptance criteria:**
- A soak benchmark: 2–8 workers, work items of 50ms–5s simulated duration, mixed
  interactive/batch arrival, ≥ 10-minute sustained run; measures queue-wait p50/p95/p99 by class,
  dispatch fairness, allocation stability.
- Run against **both** priority bindings; the README's strategy-selection guidance quotes the
  results ("ship whichever measures better and keep both honest" — issue #17).
- Soak is a release-gate artifact (manual/CI-nightly), not a per-PR gate.

### DR-9: AOT/trim posture

- `Bifrost.Concurrency` and all touched packages: `IsAotCompatible=true`, zero trim/AOT warnings
  under warnings-as-errors. `[ThreadStatic]` handles, seqlock, and
  `RuntimeHelpers.IsReferenceOrContainsReferences` are AOT-safe; verify under Bifrost's stricter
  analyzer set (DataFerry did not declare AOT).
- The orchestrator strategy selection is enum/factory-based — no reflective binding resolution.

### DR-10: DataFerry freeze (out-of-repo follow-up)

README pointer in DataFerry: "Production home: `Bifrost.Concurrency` (lvlup-sw/bifrost)". No
code changes land in DataFerry after the port commit-SHA recorded in the port PR description.

## Technical Design

### Package layout

```
src/
├── Bifrost.Concurrency/                  # NEW leaf primitive package
│   ├── ConcurrentPriorityQueue*.cs       # MultiQueue port (13 files)
│   ├── LockingPriorityQueue.cs           # lock + PriorityQueue baseline (supported binding)
│   └── Bifrost.Concurrency.csproj        # IsAotCompatible, AllowUnsafeBlocks
├── Bifrost/                              # orchestrator changes
│   ├── Queues/IWorkQueue.cs              # strategy contract
│   ├── Queues/FifoChannelWorkQueue.cs    # default binding (wraps Channel)
│   ├── Queues/ConcurrentPriorityWorkQueue.cs   # CPQ + semaphore + watermarks
│   ├── Queues/LockingPriorityWorkQueue.cs      # Lock+PQ + same composition
│   ├── WorkClass.cs / WorkEnvelope.cs / EnqueueResult.cs
│   └── WorkOrchestrator.cs               # rewritten against IWorkQueue<WorkEnvelope<TWork>>
├── Bifrost.OpenTelemetry/                # queue-wait histogram, rejected counter
└── Bifrost.Tests.Concurrency/            # NEW test project (5 ported families + contract tests)
```

### Dispatch flow (priority strategy)

```
producer ──EnqueueAsync(work, class)──► classify → envelope(work, class, nowTicks)
        → watermark check (class vs approx Count)      ── over → Rejected(Watermark) → DLQ
        → key = ticks − Boost(class)
        → cpq.TryEnqueue(envelope, key)                 ── full → Rejected(Capacity)  → DLQ
        → semaphore.Release() → Accepted

worker ──► semaphore.WaitAsync(ct) → cpq.TryDequeue (retry on spurious miss)
        → record queue-wait histogram (now − EnqueuedAtTicks, tag: class)
        → dispatch through existing handler/decorator chain
```

### Strategy contract notes

- The semaphore counts *items*, not capacity: `Release` per accepted enqueue, one `WaitAsync` per
  dequeue attempt. A `TryDequeue` miss after a successful wait (relaxed-structure transient) loops
  back to `WaitAsync` — conservation is asserted by the ported ConservationStress family adapted
  to the composed queue.
- `Count` is approximate under MultiQueue (striped); the contract documents it as "permitted
  approximate" — autoscaling and watermarks tolerate this; tests must not assert exact
  intermediate counts under concurrency.

## Integration Points

- **#16 scheduling:** orchestrator dispatch from scheduled jobs defaults to `WorkClass.Batch`,
  overridable per job in the scheduling fluent builder (`.DispatchTo<…>(…, WorkClass.Interactive)`
  for the rare interactive tick). A rejected tick enqueue is a failed fire → `JobFireFailedEvent`;
  the next occurrence is unaffected. This resolves #16's open question Q6.
- **DLQ decorator:** receives watermark/capacity rejections — same path as handler failures.
- **Autoscaling:** reads `IWorkQueue.Count` (approximate-tolerant — verify its thresholds).
- **basileus:** Stage-1 tags interactive sandbox executions `Interactive`, ingestion `Batch`;
  collects the trigger evidence; flips `DispatchStrategy` only when the pre-registered trigger
  fires.

## Testing Strategy

- Ported families (RankError, ConservationStress, SeqlockTear, ThreadChurn, EmptySemantics) run
  against the raw CPQ in `Bifrost.Tests.Concurrency`.
- `IWorkQueue` contract test suite runs against all three bindings (issue #17 requirement).
- Orchestrator integration: interactive-jumps-batch, aging bound, watermark shed order,
  rejection→DLQ routing, decorator-stack compatibility per binding.
- DR-7 no-regression benchmark with pre-change baseline; DR-8 soak as release artifact.
- Coverage: 80% line/branch/method on `Bifrost.Concurrency` and changed orchestrator code.

## Open Questions

1. **`WorkClass` shape** — enum (closed, three values) vs a small struct with an int lattice
   (open). Enum chosen provisionally for AOT-trivial serialization and switch-based boosts; revisit
   only if a consumer demonstrates a fourth class.
2. **Boost window defaults** — 30s interactive boost is a placeholder; the soak (DR-8) should
   inform the shipped default.
3. **Public CPQ name** — `Bifrost.Concurrency.ConcurrentPriorityQueue<TElement,TPriority>`
   collides by simple name with any future BCL type the proposal might produce. Acceptable
   (namespaced), but the XML docs should note the rank-error contract prominently to avoid
   "drop-in `PriorityQueue` replacement" misreading.
