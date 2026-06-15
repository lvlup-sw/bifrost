# Implementation Plan: Buffered MultiQueue — per-sub-queue insertion + deletion buffers (ESA 2021 §4)

## Source Design
Link: `docs/designs/2026-06-15-cpq-buffered-multiqueue.md`
Spike (invariants): `docs/research/2026-06-15-cpq-buffering-spike.md` · Issue #26 · Milestone #2

## Scope
**Target:** Full design (DR-1 … DR-8).
**Excluded:** Formal `perf`-stat cache-miss A/B on the Xeon (DR-8's formal half) → **#30** (no `perf`/AVX-512 locally). The default-on flip is out of scope — ships **off**.

## Summary
- Total tasks: **17**
- Parallel groups: 1 main sequential chain (SubQueue.cs hot path) + 6 parallel-safe leaf tasks
- Estimated test count: ~24 (differential/property-heavy)
- Design coverage: 8 of 8 DR sections covered

## Spec Traceability

| DR | Requirement | Tasks |
|----|-------------|-------|
| DR-1 | `[InlineArray(16)]` inline buffer storage | T1, T15, T16 |
| DR-2 | Buffered push/pop algorithm + emptiness invariant | T3, T4, T5, T6 |
| DR-3 | Zero-contract-change: published top, false-authority, occupancy | T7, T8 |
| DR-4 | Write-barrier-safe buffer moves | T10 |
| DR-5 | Opt-in `bufferCapacity` knob (default OFF); keep arity-4 | T2, T11 |
| DR-6 | Error/edge cases + `Count` integration | T5, T9, T13, T14 |
| DR-7 | `BIFROST_TEST_HOOKS` instrumentation | T12, T17 |
| DR-8 | A/B gate (merge); formal run → #30 | T15 |

## Task Breakdown

### Task 1: `SubQueueBuffer<TElement,TPriority>` inline-array storage
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** unit · **Implements:** DR-1
**testingStrategy:** propertyTests: true · benchmarks: false · characterizationRequired: false

1. [RED] `BufferLayout_InlineArray16_RoundTripsAllSlots` — File: `src/Bifrost.Tests.Concurrency/BufferedSubQueueTests.cs`. Write 16 `(element,priority)` tuples through the indexer / `CreateSpan`, read them back; assert order and that `CreateSpan(ref …, n)` length honors a logical `n ≤ 16`. Expected failure: type does not exist.
2. [GREEN] Add `src/Bifrost.Concurrency/SubQueueBuffer.cs`: `[InlineArray(SubQueue.BufferCapacityMax)] internal struct SubQueueBuffer<TElement,TPriority> { private (TElement Element,TPriority Priority) _e0; }` + a `static Span<…> AsSpan(ref SubQueueBuffer<…>, int len)` helper using `MemoryMarshal.CreateSpan(ref Unsafe.As<…>(ref buf), len)`. Const `BufferCapacityMax = 16`.
3. [REFACTOR] XML docs; confirm single-field/no-explicit-layout (no CS9168/9169).

**Dependencies:** None · **Parallelizable:** Yes (new file)

### Task 2: `bufferCapacity` constructor knob + validation
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** unit · **Implements:** DR-5
**testingStrategy:** propertyTests: false · benchmarks: false · characterizationRequired: false

1. [RED] `Ctor_BufferCapacityOutOfRange_Throws` and `Ctor_Default_BufferingDisabled` — File: `src/Bifrost.Tests.Concurrency/SubQueueEdgeTests.cs`. Assert `bufferCapacity` < 0 or > 16 throws `ArgumentOutOfRangeException` (message names the compile-time max); default ctor ⇒ `bufferCapacity == 0`. Expected failure: parameter/field absent.
2. [GREEN] `ConcurrentPriorityQueue.cs`: add `bufferCapacity` to the ctor chain alongside `boundedCapacity`/`stickiness`; `ValidateBufferCapacity` (mirror `ValidateBoundedCapacity`); store `_bufferCapacity`; thread it to each `SubQueue`.
3. [REFACTOR] Doc-comment the `[0,16]` range and 0=off semantics.

**Dependencies:** None · **Parallelizable:** Yes (ConcurrentPriorityQueue.cs ctor, distinct from SubQueue hot path)

### Task 3: Buffered push → insertion buffer + flush-on-full
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** integration · **Implements:** DR-2
**testingStrategy:** propertyTests: true · benchmarks: false · characterizationRequired: true

1. [RED] `Push_Buffered_MatchesUnbufferedOrder_InsertOnly` — File: `BufferedSubQueueTests.cs`. Differential: same random push-only sequence into a buffered (cap 16) and unbuffered sub-queue; drain both; assert identical order. Expected failure: buffered push not implemented (pushes lost / wrong order).
2. [GREEN] `SubQueue.TryLockedPush`: when `_bufferCapacity > 0`, append to the insertion buffer; on full, flush to the arity-4 heap then continue. (Sorted-`D` / direct-`D` cases land in T5.)
3. [REFACTOR] Extract `FlushInsertion()` helper.

**Dependencies:** T1 · **Parallelizable:** No (SubQueue.cs hot path)

### Task 4: Buffered pop → `D.front()` + refill-on-empty
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** integration · **Implements:** DR-2
**testingStrategy:** propertyTests: true · benchmarks: false · characterizationRequired: true

1. [RED] `Pop_Buffered_DrainsInPriorityOrder_WithRefill` — File: `BufferedSubQueueTests.cs`. Seed > 16 elements, pop all; assert ascending priority order (≥ 2 refills occur). Expected failure: pop ignores `D` / no refill.
2. [GREEN] `SubQueue.PopHeldRoot`: when buffered, return+remove `D.front()`; if `D` empties, `RefillDeletion()` = flush `I`→heap then pop `min(_bufferCapacity,|heap|)` smallest into `D` sorted.
3. [REFACTOR] Extract `RefillDeletion()`.

**Dependencies:** T3 · **Parallelizable:** No (SubQueue.cs hot path)

### Task 5: Sorted-insert into `D`, direct-to-`D`, eviction cascade
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** integration · **Implements:** DR-2, DR-6
**testingStrategy:** propertyTests: true · benchmarks: false · characterizationRequired: true

1. [RED] `Push_SmallElement_InsertsIntoDeletionBufferSorted`, `Push_DFullIFull_EvictsMaxThroughHeap` — File: `BufferedSubQueueTests.cs`. (a) Mixed push/pop with descending keys forcing `v ≤ max(D)` sorted inserts; (b) saturate `D` and `I`, push a small `v`; assert all elements conserved and global order exact vs. unbuffered. Expected failure: small elements mis-ordered / eviction loses elements.
2. [GREEN] Implement the `v ≤ max(D)` sorted-insert, the empty-structure direct-to-`D` case, and the `D`-full→evict-`max(D)`→`I` (→flush+heap-push if `I` full) cascade — per reference `BufferedPQ`.
3. [REFACTOR] Extract `ShiftInsert`/`EvictMax` over `Span`.

**Dependencies:** T4 · **Parallelizable:** No (SubQueue.cs hot path)

### Task 6: `D`-empty ⇒ empty invariant + debug assertion
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** unit · **Implements:** DR-2
**testingStrategy:** propertyTests: true · benchmarks: false · characterizationRequired: false

1. [RED] `Invariant_DeletionBufferEmpty_ImpliesSubQueueEmpty` — File: `BufferedSubQueueTests.cs`. Property: across random op sequences, whenever `deletionCount == 0` then `insertionCount == 0 && heapSize == 0`. Expected failure: invariant not maintained at some boundary.
2. [GREEN] Ensure refill/flush ordering upholds it; add a `Debug.Assert` in the pop path guarding `D`-empty-with-residue.
3. [REFACTOR] Centralize the invariant assert.

**Dependencies:** T5 · **Parallelizable:** No (SubQueue.cs hot path)

### Task 7: Publish `D.front()` as the seqlock top; `EmptyFlag` = `D`-empty
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** integration · **Implements:** DR-3
**testingStrategy:** propertyTests: false · benchmarks: false · characterizationRequired: true

1. [RED] `TryReadTop_Buffered_ReturnsDeletionBufferFront` + run existing `SeqlockTearTests` with buffering on — File: `SeqlockTearTests.cs`. Assert the lock-free read yields `D.front()`/`D`-empty and never tears; no sampling path reads a buffer field. Expected failure: top still reflects heap root.
2. [GREEN] Call `PublishTop(D.front(), empty: D.empty)` at every buffered mutation site (pop, front-changing push, refill); leave `TryReadTop` and Dequeue sampling untouched.
3. [REFACTOR] Single `RepublishTop()` helper for the buffered tail.

**Dependencies:** T6 · **Parallelizable:** No (SubQueue.cs hot path)

### Task 8: Occupancy bit on the `D` 0↔non-0 boundary
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** integration · **Implements:** DR-3
**testingStrategy:** propertyTests: false · benchmarks: false · characterizationRequired: true

1. [RED] `Occupancy_Buffered_ReflectsDeletionBufferState` — File: `src/Bifrost.Tests.Concurrency/OccupancyBitmaskTests.cs`. Assert the bit is set iff `D` non-empty and clears on `D` drain; sparse-routing still finds buffered elements. Expected failure: bit tracks heap boundary, mismatches `D`.
2. [GREEN] Move `SetOccupancyBit`/`ClearOccupancyBit` calls to the `D` 0↔non-0 transitions (replacing heap 0↔1).
3. [REFACTOR] None.

**Dependencies:** T7 · **Parallelizable:** No (SubQueue.cs hot path)

### Task 9: `Count` includes buffered elements (I + D + heap)
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** integration · **Implements:** DR-6
**testingStrategy:** propertyTests: true · benchmarks: false · characterizationRequired: true

1. [RED] `Count_Buffered_EqualsTotalResident` — File: `src/Bifrost.Tests.Concurrency/CollectionSurfaceTests.cs`. Conservation: after arbitrary buffered op sequences, `Count` equals enqueued-minus-dequeued across `I`+`D`+heap. Expected failure: `Count` reflects heap size only (under-counts buffered).
2. [GREEN] Per-sub-queue published count becomes `_insertionCount + _deletionCount + _size`; `Volatile.Write(ref _header.Count, total)`.
3. [REFACTOR] None.

**Dependencies:** T6 · **Parallelizable:** No (SubQueue.cs hot path; sequence after T8)

### Task 10: Write-barrier-safe buffer moves
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** integration · **Implements:** DR-4
**testingStrategy:** propertyTests: false · benchmarks: true · characterizationRequired: false

1. [RED] `BufferMoves_ReferenceElements_NoStaleRetention` (File: `BufferedSubQueueTests.cs`) — after flush/refill/eviction, vacated slots hold no live reference (WeakReference collectible); plus a value-type `0 B/op` assertion via the benchmark in T15. Expected failure: vacated slots retain references / barrier path wrong.
2. [GREEN] Implement all moves via `Span.CopyTo`; gate slot `Span.Clear` on `RuntimeHelpers.IsReferenceOrContainsReferences<(TElement,TPriority)>()`; **no** `MemoryMarshal.AsRef`/`Unsafe` reinterpret or SIMD on ref-containing tuples.
3. [REFACTOR] Consolidate move helpers; confirm value-type path lowers to `memmove`.

**Dependencies:** T5 · **Parallelizable:** No (SubQueue.cs hot path)

### Task 11: `bufferCapacity = 0` bypass is bit-exact unbuffered
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** acceptance · **Implements:** DR-5
**testingStrategy:** propertyTests: true · benchmarks: false · characterizationRequired: true

1. [RED] `DefaultOff_MatchesPreFeature_OrderCountRankError` — File: `BufferedSubQueueTests.cs`. Characterization: with `bufferCapacity == 0`, drain order, `Count`, and rank-error distribution are identical to the unbuffered queue over identical seeds. Expected failure: off-path diverges (branch leaks into unbuffered behavior).
2. [GREEN] Ensure the `_bufferCapacity > 0` branch fully bypasses buffers when off; the unbuffered code path is unchanged (heap root published, heap boundary occupancy, `Count = _size`).
3. [REFACTOR] Hoist the branch; confirm a single predictable compare.

**Dependencies:** T2, T7, T9 · **Parallelizable:** No (SubQueue.cs hot path)

### Task 12: `BIFROST_TEST_HOOKS` buffer counters
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** unit · **Implements:** DR-7
**testingStrategy:** propertyTests: false · benchmarks: false · characterizationRequired: false

1. [RED] `Counters_BufferedRun_RecordFlushRefillHitEvict` — File: `BufferedSubQueueTests.cs`. Assert flush/refill/buffered-hit/direct-`D`/eviction counters increment as expected over a scripted sequence. Expected failure: counters/getters absent.
2. [GREEN] Add fields + `…ForTest` getters **ungated** (CS0649-suppressed); increment **sites** behind `#if BIFROST_TEST_HOOKS` — mirroring `_debugOccupancyWriteCount`.
3. [REFACTOR] Group counters in a `#region`.

**Dependencies:** T5 · **Parallelizable:** No (SubQueue.cs; sequence after T10)

### Task 13: Bounded-capacity admission parity with buffering on
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** integration · **Implements:** DR-6
**testingStrategy:** propertyTests: false · benchmarks: false · characterizationRequired: true

1. [RED] `BoundedAdmission_Buffered_MatchesUnbufferedShedding` — File: `src/Bifrost.Tests.Concurrency/BoundedCapacityEdgeTests.cs`. At a fixed bound + total population, watermark admit/`EnqueueResult` shedding is identical buffered vs. unbuffered. Expected failure: buffered residents miscounted at the door → over-admit.
2. [GREEN] No new code if T9's total-Count is correct; this task **proves** the integration (add only glue if a gap surfaces).
3. [REFACTOR] None.

**Dependencies:** T9 · **Parallelizable:** Yes (test-only; ConcurrentPriorityQueue admission already in place)

### Task 14: Refill partial-fill + tiny-structure edges
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** integration · **Implements:** DR-6
**testingStrategy:** propertyTests: true · benchmarks: false · characterizationRequired: false

1. [RED] `Refill_HeapSmallerThanCapacity_FillsExactly`, `TinyQueue_StaysInDeletionBuffer_NeverTouchesHeap` — File: `BufferedSubQueueTests.cs`. (a) `|heap| < 16` ⇒ `D` gets exactly `|heap|`, heap empties; (b) a ≤16-element lifetime never pushes to the heap (assert via T12 counters). Expected failure: partial refill mis-sizes / tiny queue spills to heap.
2. [GREEN] Bound refill by `min(_bufferCapacity, |heap|)`; honor the direct-to-`D` empty-structure case.
3. [REFACTOR] None.

**Dependencies:** T5, T12 · **Parallelizable:** No (SubQueue.cs hot path)

### Task 15: A/B benchmark verb + `0 B/op` gate (value + reference)
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** integration · **Implements:** DR-8, DR-1
**testingStrategy:** propertyTests: false · benchmarks: true · characterizationRequired: false

1. [RED] Add a buffered/unbuffered A/B verb + a `[MemoryDiagnoser]` buffered Enqueue+Dequeue benchmark (int and string/object) asserting `0 B/op` — File: `src/Bifrost.Benchmarks/Concurrency/` (extend `ThroughputVerbs.cs` / `CpqSingleThreadedLatencyBenchmarks.cs`). Expected failure: no toggle wired / allocation observed.
2. [GREEN] Thread `bufferCapacity` through the harness; capture the dense-throughput + latency-vs-depth A/B.
3. [REFACTOR] Record results under `docs/benchmarks/2026-06-cpq-buffering-ab.md` with the GO/NO-GO decision; link #30 for the formal `perf` run.

**Dependencies:** T11 · **Parallelizable:** Yes (Bifrost.Benchmarks project)

### Task 16: AOT-smoke exercises the buffered reference path
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** integration · **Implements:** DR-1
**testingStrategy:** propertyTests: false · benchmarks: false · characterizationRequired: false

1. [RED] Extend the AOT-smoke sample to construct a `bufferCapacity:16` `ConcurrentPriorityQueue<object,long>`, enqueue+dequeue, and assert real dispatch — File: `samples/` AOT smoke (per the scheduling-AOT precedent). Expected failure: buffered path not exercised under NativeAOT.
2. [GREEN] Wire the buffered enqueue/dequeue; ensure the publish is IL2xxx/IL3050-clean.
3. [REFACTOR] None.

**Dependencies:** T11 · **Parallelizable:** Yes (sample project)

### Task 17: DR-7 amortization assertion (heap refills ≈ pops / C)
**Phase:** RED → GREEN → REFACTOR · **Test Layer:** property · **Implements:** DR-7
**testingStrategy:** propertyTests: true · benchmarks: false · characterizationRequired: false

1. [RED] `Amortization_SteadyBufferedRun_RefillsApproxPopsOverCapacity` — File: `BufferedSubQueueTests.cs`. Over a steady run, refill-count ≈ pops / `bufferCapacity` (±tolerance), proving the locality mechanism. Expected failure: refill frequency off (heap touched too often).
2. [GREEN] No new production code — consumes T12 counters; add only the assertion harness.
3. [REFACTOR] None.

**Dependencies:** T12 · **Parallelizable:** Yes (test-only)

## Parallelization Strategy

The feature is dominated by a **single sequential chain through `SubQueue.cs`** (the hot type) — parallel worktrees on it would only churn merge conflicts (per the [[exarchos-delegation-ops]] lesson). Plan accordingly:

- **Wave 0 (parallel):** T1 (new `SubQueueBuffer.cs`), T2 (`ConcurrentPriorityQueue.cs` ctor).
- **Sequential core (one worktree):** T3 → T4 → T5 → T6 → T7 → T8 → T9 → T10 → T11; T12 and T14 fold into this chain after T10/T5.
- **Parallel-safe leaves (own worktrees, once deps met):** T13 (bounded tests), T15 (Benchmarks), T16 (AOT sample), T17 (amortization test). T13 after T9; T15/T16 after T11; T17 after T12.

Recommended dispatch: 2 agents in Wave 0, then 1 implementer owns the SubQueue core chain, with the 4 leaf tasks farmed to short-lived worktree agents as their dependencies clear.

## Deferred Items

- **Formal `perf`-stat cache-miss A/B (Xeon n=256/64T)** → #30 (DR-8 formal half) — no `perf`/AVX-512 locally.
- **Default-on flip** → tracked against #30; this feature ships `bufferCapacity` default 0.
- **`BufferCapacityMax > 16`** → one-line const bump + rebuild if a future spike wants to confirm the paper's >16 quality degradation on Bifrost (Open Question in the design).
- **Knob naming (`bufferCapacity` int vs `enableBuffering` bool)** → resolved to the int `[0,16]` for BCL-ctor consistency + the A/B-sweep dial; revisit only if review objects.

## Completion Checklist
- [ ] All tests written before implementation (TDD; verify via `check_tdd_compliance`)
- [ ] All tests pass in **Release** (Debug-green ≠ Release-green for async/timing)
- [ ] `0 B/op` preserved on value-type **and** reference instantiations
- [ ] Default-off path bit-exact vs. pre-feature (T11 characterization)
- [ ] Coverage gate green; AOT publish warning-clean
- [ ] A/B captured + GO/NO-GO recorded; #30 linked for the formal run
- [ ] Ready for review
