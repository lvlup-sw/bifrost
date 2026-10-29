# Implementation Plan: CPQ Port + Priority-Aware Work Dispatch

**Design:** `docs/designs/2026-06-12-cpq-port-priority-dispatch.md`
**Issue:** [#17](https://github.com/lvlup-sw/bifrost/issues/17)
**Iron Law:** No production code without a failing test first.

> Port-task TDD shape: tests are ported **first** and fail (missing types), then implementation
> files are ported to green — the discipline holds even when the code pre-exists.
> Tests are TUnit on Microsoft.Testing.Platform (`dotnet test --project src/<project>`);
> **assertions must be awaited**. Package versions live in `src/Directory.Packages.props` (CPM).

## Scope

**Included (full vertical, default-off — per ideation decisions 2026-06-12):**
- New `Bifrost.Concurrency` leaf package: MultiQueue `ConcurrentPriorityQueue<TElement,TPriority>`
  + `LockingPriorityQueue<TElement,TPriority>` ported from `lvlup-sw/DataFerry` with all five test
  families and benchmarks.
- Stage 1: `WorkClass` tagging, `WorkEnvelope<TWork>`, queue-wait-by-class histogram,
  rejected counter.
- `IWorkQueue<T>` strategy abstraction; orchestrator rewritten against it; FIFO default binding;
  two priority bindings (CPQ, Lock+PQ); `DispatchStrategy` selection.
- Virtual-time priority key + watermark admission + typed rejection (**breaking**:
  `EnqueueAsync` → `ValueTask<EnqueueResult>`; `IWorkOrchestrator.Writer` escape hatch removed).
- DR-7 no-regression gate (baseline captured before any orchestrator change) and DR-8
  consumer-shaped soak.

**Excluded:** enabling the priority strategy in any consumer (evidence-gated in basileus);
DataFerry repo freeze (out-of-repo manual step, Task 31); per-class backpressure policies beyond
watermarks (v2 territory).

## Design-delta notes (surfaced during planning)

1. `IWorkOrchestrator.Writer` (`ChannelWriter<TWork>` escape hatch) cannot survive the queue
   abstraction — **removed** under the approved breaking change (Task 13). Design §DR-6 implies
   but does not name this; flagged for plan-review.
2. `CreateWorkerFunction` (autoscaling's dynamic workers) iterates `_channel.Reader` directly —
   rewritten against `IWorkQueue` (Task 17), otherwise dynamic workers would bypass priority
   ordering entirely.
3. The orchestrator has no `TimeProvider`; envelope timestamps and queue-wait math require one —
   injected ctor param defaulting to `TimeProvider.System` (Task 12), consistent with #16's
   banned-API rule.

## Traceability

| Design requirement | Tasks |
|---|---|
| DR-1: CPQ port into Bifrost.Concurrency | 2–10 |
| DR-2: Work classes (Stage 1) | 11, 12, 13, 14 |
| DR-3: Queue-wait instrumentation | 23, 24 |
| DR-4: IWorkQueue strategy contract | 15, 16, 17, 18 |
| DR-5: Virtual-time key + bounded starvation | 19, 20, 25, 26 |
| DR-6: Watermark admission + typed rejection | 11, 13, 21, 23 |
| DR-7: FIFO no-regression gate | 1, 28 |
| DR-8: Consumer-shaped soak | 29 |
| DR-9: AOT/trim posture | 9, 30 |
| DR-10: DataFerry freeze | 31 (out-of-repo) |

---

## Group 0 — Baseline (FIRST, before any orchestrator change)

### Task 1: Capture FIFO performance baseline
**Phase:** measurement (no TDD — produces the DR-7 comparison artifact)

1. Add `OrchestratorBaselineBenchmarks` to `src/Bifrost.Benchmarks/Orchestrator/`:
   enqueue→dispatch round-trip latency + `[MemoryDiagnoser]` allocations, WorkerCount ∈ {1, 2, 8},
   Capacity 128, no-op handler.
2. Run on the **pre-change** orchestrator; commit results to
   `docs/benchmarks/2026-06-cpq-orchestrator-baseline.md` + raw data under `docs/benchmarks/data/`.

**Dependencies:** None. **Parallelizable:** Yes (but MUST complete before Tasks 13/17 merge).

---

## Group A — Bifrost.Concurrency package (parallel with Groups B/C)

### Task 2: Scaffold Bifrost.Concurrency + Bifrost.Tests.Concurrency
**Phase:** scaffold + build smoke

1. `src/Bifrost.Concurrency/Bifrost.Concurrency.csproj`: `IsPackable=true`,
   `AllowUnsafeBlocks=true`, package metadata (description quotes the rank-error contract);
   inherits `IsAotCompatible=true` from `Directory.Build.props`.
2. `src/Bifrost.Tests.Concurrency/` (TUnit, `IsAotCompatible=false`, ProjectReference to
   Concurrency only). Solution wiring in `src/Bifrost.sln`.
3. Smoke test `PackageSmoke_Builds_NamespaceResolves` (awaited TUnit assertion).

**Dependencies:** None. **Parallelizable:** Yes.

### Task 3: Port CPQ implementation behind EmptySemantics family
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Port `EmptySemanticTests.cs` →
   `src/Bifrost.Tests.Concurrency/MultiQueue/EmptySemanticTests.cs`, namespace
   `Bifrost.Concurrency` — fails: types missing.
2. **[GREEN]** Port all 13 implementation files from
   `DataFerry/src/DataFerry/Concurrency/MultiQueue/` → `src/Bifrost.Concurrency/`
   (`ConcurrentPriorityQueue` shell + `.Enqueue/.Dequeue/.Count/.Collection/.Inspect` partials,
   `SubQueue`, `SubQueueHeader`, `PaddedTopSlot`, `ThreadHandle`, `PriorityComparerHelpers`,
   `SubQueuePopStatus`, debug view). Namespace migration
   `lvlup.DataFerry.Concurrency*` → `Bifrost.Concurrency`. Public API surface preserved verbatim
   (dual-mode dequeue, opt-in bounding, unordered enumeration).
3. **[REFACTOR]** Bifrost analyzer/style compliance (Lvlup.Build warnings-as-errors); XML docs
   carry the `(5/6)·n` rank-error contract verbatim; record the DataFerry source SHA in the file
   headers' port note.

**Dependencies:** Task 2. **Parallelizable:** No (single port unit).

### Task 4: Port ConservationStressTests
**Phase:** RED → GREEN — port family; failures indicate porting defects, fix until green.
**Dependencies:** Task 3. **Parallelizable:** Yes (with 5, 6, 7).

### Task 5: Port RankErrorTests (empirical rank-error gate)
**Phase:** RED → GREEN — port family incl. the statistical gate thresholds unchanged.
**Dependencies:** Task 3. **Parallelizable:** Yes (with 4, 6, 7).

### Task 6: Port SeqlockTearTests
**Phase:** RED → GREEN.
**Dependencies:** Task 3. **Parallelizable:** Yes (with 4, 5, 7).

### Task 7: Port ThreadChurnTests
**Phase:** RED → GREEN — `[ThreadStatic]` handle safety under pool churn and across `await`.
**Dependencies:** Task 3. **Parallelizable:** Yes (with 4, 5, 6).

### Task 8: Port LockingPriorityQueue (rename from NaiveConcurrentPriorityQueue)
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Port + rename `NaiveConcurrentPriorityQueueTests` →
   `LockingPriorityQueueTests.cs`; add `LockingPriorityQueue_NameConveysSupportedBinding_XmlDocs`
   presence test.
2. **[GREEN]** Port `NaiveConcurrentPriorityQueue.cs` →
   `src/Bifrost.Concurrency/LockingPriorityQueue.cs` (`System.Threading.Lock` +
   `PriorityQueue<TElement,TPriority>`); it ships as a supported binding, not a strawman.
3. **[REFACTOR]** Align XML docs: when to prefer it (low contention, exact ordering).

**Dependencies:** Task 2. **Parallelizable:** Yes (independent of Task 3).

### Task 9: AOT/trim verification for Bifrost.Concurrency
**Phase:** RED → GREEN

1. **[RED]** Enable trim/AOT analyzers warnings-as-errors for the package (already repo default —
   verify no suppressions snuck in via the port); add arch test
   `Concurrency_NoReflectionApis_BannedSymbolsClean` (no `Type.GetType`, no
   `Activator.CreateInstance`, no `System.Reflection` beyond debugger attributes).
2. **[GREEN]** Fix any analyzer findings (DataFerry did not build under `IsAotCompatible`).

**Dependencies:** Tasks 3, 8. **Parallelizable:** No.

### Task 10: Port CPQ benchmarks
**Phase:** port + verify run

1. Port DataFerry benchmark suites → `src/Bifrost.Benchmarks/Concurrency/` including the
   rank-error empirical gate verb; verify `--job Dry` smoke run.
2. Commit indicative results doc `docs/benchmarks/2026-06-cpq-port-parity.md` (port-parity check
   vs DataFerry's published numbers — same order of magnitude, 0 B/op preserved).

**Dependencies:** Tasks 3, 8. **Parallelizable:** Yes (with Task 9).

---

## Group B — Stage 1 contract types (parallel with Group A)

### Task 11: WorkClass + EnqueueResult contract types
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** `src/Bifrost.Tests/Core/WorkClassTests.cs`:
   - `WorkClass_Values_OrderedInteractiveDefaultBatch`
   - `EnqueueResult_Accepted_HasNoRejectionReason`
   - `EnqueueResult_Rejected_CarriesReason` (`CapacityExceeded` | `WatermarkExceeded` | `Shutdown`)
2. **[GREEN]** `src/Bifrost.Core/WorkClass.cs`, `src/Bifrost.Core/EnqueueResult.cs`
   (readonly struct + reason enum).
3. **[REFACTOR]** XML docs: at-least-once posture unaffected; rejection is an admission outcome.

**Dependencies:** None. **Parallelizable:** Yes.

### Task 12: WorkEnvelope + TimeProvider injection
**Phase:** RED → GREEN

1. **[RED]** `WorkEnvelopeTests.cs`:
   - `WorkEnvelope_Construct_CapturesTicksFromTimeProvider` (FakeTimeProvider)
   - `WorkEnvelope_IsReadonlyRecordStruct_NoAllocation`
   - `Orchestrator_DefaultCtor_UsesSystemTimeProvider`
2. **[GREEN]** `src/Bifrost/WorkEnvelope.cs`
   (`readonly record struct WorkEnvelope<TWork>(TWork Work, WorkClass Class, long EnqueuedAtTicks)`);
   `WorkOrchestrator` ctor gains `TimeProvider? timeProvider = null` → `TimeProvider.System`.

**Dependencies:** Task 11. **Parallelizable:** Yes.

### Task 13: Breaking enqueue surface — EnqueueResult return, WorkClass param, Writer removal
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** `OrchestratorEnqueueSurfaceTests.cs`:
   - `EnqueueAsync_Fifo_ReturnsAcceptedAfterWait`
   - `EnqueueAsync_AfterShutdown_ReturnsRejectedShutdown`
   - `EnqueueAsync_DefaultsWorkClassDefault`
   - `Writer_Property_NoLongerExists` (compile-shape via reflection-free contract test on the
     interface in the test project)
2. **[GREEN]** `IWorkOrchestrator<TWork>`:
   `ValueTask<EnqueueResult> EnqueueAsync(TWork work, WorkClass workClass = WorkClass.Default, CancellationToken ct = default)`;
   **remove `Writer`**; channel becomes `Channel<WorkEnvelope<TWork>>`; `TryEnqueue`/`Run`/`TryRun`
   wrap envelopes (class param overloads).
3. **[REFACTOR]** Update every existing call site/test; CHANGELOG draft entry (finalized Task 30).

**Dependencies:** Tasks 1 (baseline first), 11, 12. **Parallelizable:** No (wide-touch break).

### Task 14: Classifier delegate option
**Phase:** RED → GREEN

1. **[RED]** `ClassifierOptionTests.cs`:
   - `Classifier_AppliedWhenPerCallIsDefault`
   - `PerCallClass_NonDefault_WinsOverClassifier`
2. **[GREEN]** `WorkOrchestratorOptions.Classifier: Func<TWork, WorkClass>?` (options are
   per-orchestrator generic — verify options pattern; if non-generic today, add
   `WorkOrchestratorOptions<TWork>` shim or builder-level classifier).

**Dependencies:** Task 13. **Parallelizable:** Yes.

---

## Group C — IWorkQueue abstraction

### Task 15: IWorkQueue contract + abstract contract-test suite
**Phase:** RED (suite definition is the deliverable)

1. **[RED]** `src/Bifrost.Tests/Queues/WorkQueueContractTests.cs` — abstract TUnit base:
   - `TryEnqueue_WhenBelowCapacity_ReturnsTrue`
   - `TryEnqueue_WhenAtCapacity_ReturnsFalse`
   - `WaitToDequeueAsync_AfterEnqueue_CompletesTrue`
   - `WaitToDequeueAsync_OnShutdown_CompletesFalse`
   - `TryDequeue_AfterWait_YieldsItem_ToleratesSpuriousMissRetry`
   - `Count_Approximate_WithinDocumentedBounds`
   - `Conservation_NItemsIn_NItemsOut_MultiProducerConsumer`
2. **[GREEN]** `src/Bifrost/Queues/IWorkQueue.cs` interface only (no binding yet — suite stays
   abstract/unbound, compiles).

**Dependencies:** None. **Parallelizable:** Yes.

### Task 16: FifoChannelWorkQueue default binding
**Phase:** RED → GREEN — concrete contract-suite subclass fails until
`src/Bifrost/Queues/FifoChannelWorkQueue.cs` wraps the bounded channel (Wait-mode internal,
`TryEnqueue` = `TryWrite` for the contract; async accept path preserved for the orchestrator).
**Dependencies:** Task 15. **Parallelizable:** Yes.

### Task 17: Rewrite WorkOrchestrator against IWorkQueue
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Existing orchestrator behavior tests are the harness (must stay green); add:
   - `WorkerLoop_UsesWaitTryDequeue_NotChannelReader`
   - `CreateWorkerFunction_DynamicWorkers_RouteThroughWorkQueue` (autoscaling path — design-delta 2)
   - `PendingCount_DelegatesToQueueCount`
   - `DrainAsync_StopAsync_DisposeAsync_SemanticsPreserved`
2. **[GREEN]** `WorkOrchestrator` holds `IWorkQueue<WorkEnvelope<TWork>>`; static + dynamic worker
   loops use `WaitToDequeueAsync`/`TryDequeue` with the retry contract; queue-wait measurement
   hook (ticks delta) recorded at dequeue (consumed by Task 24).
3. **[REFACTOR]** Delete dead channel plumbing.

**Dependencies:** Tasks 13, 16. **Parallelizable:** No (core).

### Task 18: DispatchStrategy selection
**Phase:** RED → GREEN

1. **[RED]** `DispatchStrategyTests.cs`:
   - `Options_Default_IsFifo`
   - `Builder_UsePriorityDispatch_SelectsCpqBinding`
   - `Builder_UsePriorityDispatch_LockingVariant_SelectsLockBinding`
   - `Bindings_AreSealed_ForDevirtualization`
2. **[GREEN]** `WorkOrchestratorOptions.DispatchStrategy` enum (`Fifo`, `PriorityMultiQueue`,
   `PriorityLocking`) + factory in DI/builder (`WorkOrchestratorBuilder`), enum/factory-based —
   no reflective resolution (DR-9).

**Dependencies:** Task 17 (+20, 22 for end-to-end; config layer testable with FIFO alone).
**Parallelizable:** Yes.

---

## Group D — Priority machinery

### Task 19: Virtual-time priority key
**Phase:** RED → GREEN

1. **[RED]** `PriorityKeyTests.cs`:
   - `Compute_SameClass_PreservesFifoOrder`
   - `Compute_Interactive_JumpsBoostWindowAhead`
   - `Compute_BatchOlderThanBoostWindow_OutranksFreshInteractive` (starvation bound by construction)
   - `Compute_DefaultClass_ZeroBoost`
2. **[GREEN]** `src/Bifrost/Queues/PriorityKey.cs` static compute
   (`envelope.EnqueuedAtTicks − Boost(class)`); `PriorityDispatchOptions`
   (`InteractiveBoostWindow` default 30s, `BatchPenaltyWindow` default 0).

**Dependencies:** Tasks 11, 12. **Parallelizable:** Yes.

### Task 20: ConcurrentPriorityWorkQueue (CPQ binding)
**Phase:** RED → GREEN → REFACTOR

1. **[RED]** Contract-suite subclass + composition-specific:
   - `WaitToDequeueAsync_SemaphoreCountsItems_ReleasePerEnqueue`
   - `TryDequeue_SpuriousMiss_LoopsBackToWait_NoItemLost`
   - `Conservation_ComposedQueue_UnderStress` (adapted ConservationStress)
2. **[GREEN]** `src/Bifrost/Queues/ConcurrentPriorityWorkQueue.cs` —
   `Bifrost.Concurrency.ConcurrentPriorityQueue<WorkEnvelope<TWork>, long>` + `SemaphoreSlim` +
   `PriorityKey`.
3. **[REFACTOR]** Document the relaxed-ordering note on the binding's XML docs.

**Dependencies:** Tasks 3, 15, 19. **Parallelizable:** Yes (with 22).

### Task 21: Watermark admission
**Phase:** RED → GREEN

1. **[RED]** `WatermarkAdmissionTests.cs`:
   - `TryEnqueue_Batch_RejectedAbove90Percent`
   - `TryEnqueue_Default_RejectedAbove95Percent`
   - `TryEnqueue_Interactive_AdmittedToFullCapacity`
   - `Watermarks_ToleratesApproximateCount` (striped-count slop)
   - `ShedOrder_UnderPressure_BatchFirstThenDefault`
2. **[GREEN]** Watermark check in both priority bindings before structure insert
   (`PriorityDispatchOptions.Watermarks` per class, configurable).

**Dependencies:** Task 20. **Parallelizable:** No (extends 20's binding; mirrored into 22).

### Task 22: LockingPriorityWorkQueue (Lock+PQ binding)
**Phase:** RED → GREEN — contract-suite subclass + same semaphore/watermark composition over
`Bifrost.Concurrency.LockingPriorityQueue`; exact (non-relaxed) ordering asserted as the
distinguishing test.
**Dependencies:** Tasks 8, 15, 19, 21 (watermark component shared). **Parallelizable:** Yes (with 20/21 stream).

### Task 23: Rejection routing — DLQ + rejected counter
**Phase:** RED → GREEN

1. **[RED]** `RejectionRoutingTests.cs`:
   - `EnqueueRejected_WithDlqDecorator_RoutesToDeadLetter`
   - `EnqueueRejected_WithoutDlq_SurfacesResultOnly`
   - `RejectedCounter_TaggedByClassAndReason`
2. **[GREEN]** Orchestrator enqueue path maps binding rejection → `EnqueueResult.Rejected(reason)`;
   DLQ decorator subscribes; `bifrost.orchestrator.rejected` counter in `OrchestratorMetrics`.

**Dependencies:** Tasks 18, 21. **Parallelizable:** No.

---

## Group E — Instrumentation

### Task 24: Queue-wait histogram by class
**Phase:** RED → GREEN

1. **[RED]** `QueueWaitMetricsTests.cs` (FakeTimeProvider):
   - `QueueWait_RecordedAtDequeue_TaggedByClass`
   - `QueueWait_UsesTimeProviderTicks_NotWallClock`
   - `ExistingMetrics_DepthDurationWorkers_Unchanged`
2. **[GREEN]** `bifrost.orchestrator.queue_wait` histogram (ms) in
   `src/Bifrost.OpenTelemetry/OrchestratorMetrics.cs`, fed from Task 17's dequeue hook; document
   the Stage-2 evidence recipe (interactive p95 > 500ms with batch co-resident) in the metric's
   XML docs + README.

**Dependencies:** Task 17. **Parallelizable:** Yes.

---

## Group F — Acceptance + integration

### Task 25: Interactive-jumps-batch acceptance
**Phase:** RED → GREEN — `PriorityDispatchAcceptanceTests`:
`Interactive_EnqueuedBehindNBatch_DispatchesNext_ModuloRelaxation` (N=64, multi-producer/
multi-consumer, statistical bound per documented rank error) — run against **both** bindings.
**Dependencies:** Tasks 18, 20, 22. **Parallelizable:** Yes.

### Task 26: Aging bound acceptance
**Phase:** RED → GREEN — `Batch_UnderSustainedInteractiveLoad_DispatchedWithinBoostWindowBound`
(FakeTimeProvider-driven where feasible; wall-clock-bounded stress otherwise) — both bindings.
**Dependencies:** Tasks 18, 20, 22. **Parallelizable:** Yes.

### Task 27: Decorator-stack compatibility matrix
**Phase:** RED → GREEN — autoscaling (Count approximate-tolerance — verify thresholds), DLQ,
health checks, event stream: existing decorator test suites parameterized over all three
strategies.
**Dependencies:** Tasks 18, 20, 22, 23. **Parallelizable:** Yes.

---

## Group G — Gates, docs, out-of-repo

### Task 28: DR-7 no-regression comparison
**Phase:** gate — rerun Task 1 benchmarks on the rewritten FIFO path; gate ≤5% mean regression,
no new steady-state enqueue allocations; results appended to the baseline doc. On failure: the
fallback is Approach C (parallel orchestrator) per design — escalate, do not merge.
**Dependencies:** Tasks 1, 17, 18. **Parallelizable:** No (release-gating).

### Task 29: DR-8 consumer-shaped soak
**Phase:** build + run — soak harness (2–8 workers, 50ms–5s simulated work, mixed
interactive/batch arrivals, ≥10 min): queue-wait p50/p95/p99 by class, fairness, allocation
stability; both priority bindings; nightly CI wiring; results doc
`docs/benchmarks/2026-06-cpq-soak.md`; README strategy-selection guidance quotes results
("ship whichever measures better").
**Dependencies:** Tasks 20, 21, 22. **Parallelizable:** Yes.

### Task 30: Docs + CHANGELOG + migration note
**Phase:** docs — CHANGELOG (breaking: `EnqueueAsync` signature, `Writer` removal; migration
snippets); README positioning (Competitive-honesty: when NOT to use priority dispatch); XML-doc
audit (rank-error contract prominent on CPQ + binding); verify package descriptions use only
defensible AOT phrasing (#16 DR-13 language).
**Dependencies:** Tasks 13, 23, 28. **Parallelizable:** Yes.

### Task 31: DataFerry freeze (out-of-repo, manual)
README pointer in `lvlup-sw/DataFerry`: "Production home: Bifrost.Concurrency (lvlup-sw/bifrost)";
record the port-source SHA (from Task 3's port note) in the DataFerry README and the bifrost PR
description. No further DataFerry code changes.
**Dependencies:** Task 3 merged. **Parallelizable:** Yes.

---

## Parallelization

```
T1 (baseline) ──────────────────────────────┐
T2 ─► T3 ─► {T4,T5,T6,T7} ─► T9             │
  └─► T8 ──────────────┬───► T10            │
T11 ─► T12 ───────────────────► T13 ◄───────┘─► T14
T15 ─► T16 ───────────────────► T17 ─► T18 ─► T23
T11,T12 ─► T19 ─► T20 ─► T21 ─► T22
T17 ─► T24        {T18,T20,T22} ─► {T25,T26,T27}
{T1,T17,T18} ─► T28   {T20,T21,T22} ─► T29   {T13,T23,T28} ─► T30   T3 ─► T31
```

Three independent streams until convergence at T13/T17: **Group A** (Concurrency package),
**Group B** (contract types), **T15/T16** (abstraction). Worktree-safe.

## Summary

- 31 tasks (30 in-repo + 1 manual out-of-repo); 10 design requirements covered.
- Breaking changes concentrated in Task 13 (one reviewable commit).
- Two release gates: T28 (no-regression) and T29 (soak informs default guidance).
- Estimated test additions: ~70 new + 5 ported families (~y portion of DataFerry's 128).

## Completion checklist

- [ ] All 30 in-repo tasks green; coverage gate ≥80% on Bifrost.Concurrency + changed code
- [ ] T28 gate: FIFO ≤5% mean regression, no new enqueue allocations
- [ ] T29 soak results published; README guidance quotes them
- [ ] Zero trim/AOT warnings; banned-symbols arch test green
- [ ] CHANGELOG migration notes for EnqueueAsync/Writer
- [ ] DataFerry freeze README landed (out-of-repo)
