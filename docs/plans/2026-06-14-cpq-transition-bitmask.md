# Implementation Plan: CPQ Transition Bitmask

**Feature ID:** `cpq-transition-bitmask`
**Design:** [`docs/designs/2026-06-14-cpq-transition-bitmask.md`](../designs/2026-06-14-cpq-transition-bitmask.md)
**Date:** 2026-06-14
**Branch:** `feature/cpq-transition-bitmask` (off `main`, where the CPQ lives)

## Scope

**Full** implementation of the Approach-A floor (bitmask-as-hint, verification scan stays the empty
authority) plus the committed before/after benchmark artifact. The O(1)-empty snapshot protocol and
SIMD are **spike-gated** (conditional Tasks 23–24) — built only if the discover spike returns *B-go*.

**Sequencing is spike-first** (design DR-6). Tasks 1–5 run as a separate `/exarchos:discover` cycle
*before* the production tasks are delegated; the findings doc + go/no-go verdicts gate downstream
work. The plan-review human checkpoint is where the spike is kicked off.

## Traceability summary

| DR | Requirement | Tasks |
|----|-------------|-------|
| DR-1 | Per-instance occupancy bitmask | 6, 7 |
| DR-2 | Boundary-only transition writes, under lock | 8, 9, 10, 11 |
| DR-3 | Sparse-fallback routing (Approach A) | 12, 13, 14 |
| DR-4 | Staleness safety + emptiness contract | 15, 16, 17 |
| DR-5 | Zero dense regression, zero allocation | 2, 20, 21 |
| DR-6 | Discover spike + B-graduation gate | 1, 2, 3, 4, 5, 23, 24 |
| DR-7 | Before/after benchmark suite | 3, 18, 19, 20, 22 |

Task IDs are sequential for tooling; the bracketed group label (e.g. `[S1]`, `[T2]`) carries the
design grouping. Every task names its `**Implements:** DR-N` anchor.

## Verification-ladder stamps

Concurrency-mutating tasks (transition writes, dequeue control flow, churn stress) are **high-tier**
(`boundaryTouching: true`) — full RED→GREEN→REFACTOR. Pure indexing math and no-op-assertion tasks
are **medium**. Benchmark/measurement tasks are **low** (harness code, gated by numbers). Spike
tasks (1–5) are research — no TDD; the deliverable is the findings doc.

---

## Group S — Discovery Spike (DR-6) · research, not TDD · gates all production tasks

> **Execution note:** Run via `/exarchos:discover` on a throwaway branch. Produces
> `docs/research/2026-06-14-cpq-bitmask-spike.md`. No shippable code — the production tasks
> re-implement under TDD. Spike branch deleted after findings are captured (repo convention).

### Task 1: [S1] Scalar prototype (occupancy + routing, minimal)
**Phase:** Research · **riskTier:** low · **Implements:** DR-6
- Wire a minimal `ulong[]` occupancy + Phase-1.5 routing into a prototype branch (no tests/polish)
  sufficient to measure.
- **Deliverable:** runnable prototype.

**Dependencies:** None · **Parallelizable:** No

### Task 2: [S2] Dense-regression measurement
**Phase:** Research · **riskTier:** low · **benchmarks:** true · **Implements:** DR-6, DR-5
- UniformMixed5050 @ 64T, prototype vs baseline. Confirm `_occupancy` write count ≈0 under load
  ("never written when dense").
- **Deliverable:** number + verdict feeding the DR-5 gate. **Exit:** dense within ±2% or escalate.

**Dependencies:** 1 · **Parallelizable:** No

### Task 3: [S3] Sparse-win measurement
**Phase:** Research · **riskTier:** low · **benchmarks:** true · **Implements:** DR-6, DR-7
- Population-10 pair latency, prototype vs baseline (~260 ns target to beat).
- **Deliverable:** quantified routing-bounded floor that sizes the DR-5/DR-7 targets.

**Dependencies:** 1 · **Parallelizable:** Yes (with Task 2)

### Task 4: [S4] Approach-2 linearizability analysis (B-go/no-go)
**Phase:** Research · **riskTier:** low · **Implements:** DR-6
- Build a false-empty stress harness (single-item producers vs drainers) attacking a stably-all-zero
  read used as an authoritative empty; write the linearization argument for a double-read /
  generation-stamp protocol under the .NET memory model (arm64 included).
- **Deliverable:** explicit **B-go** or **B-no-go** verdict → gates Tasks 23–24.

**Dependencies:** 1 · **Parallelizable:** Yes

### Task 5: [S5] SIMD probe + findings doc + branch teardown
**Phase:** Research · **riskTier:** low · **Implements:** DR-6
- Measure `Vector256` occupancy read vs scalar `BitOperations` on the sparse path; recommend (no
  code). Assemble `docs/research/2026-06-14-cpq-bitmask-spike.md` with all four measurements + the B
  verdict + SIMD recommendation. Delete the spike branch/worktree.
- **Deliverable:** committed findings doc.

**Dependencies:** 2, 3, 4 · **Parallelizable:** No

---

## Group F — Foundation (DR-1) · depends on Group S

### Task 6: [F1] Occupancy indexing helpers
**Phase:** RED → GREEN → REFACTOR · **riskTier:** medium · **Implements:** DR-1
Implements the design's **Data structure and indexing** section.
1. [RED] `OccupancyBitmask_WordAndBit_MapIndexCorrectly` and
   `OccupancyBitmask_SizedForSubQueueCount_MasksUnusedHighBits`
   - File: `src/Bifrost.Tests.Concurrency/OccupancyBitmaskTests.cs`
   - Expected failure: helpers/sizing do not exist yet.
2. [GREEN] Add `word(i)=i>>6`, `bit(i)=1UL<<(i&63)`, array length `(n+63)>>6`; high-bit masking for
   non-multiple-of-64 `n` (e.g. 32).
   - File: `src/Bifrost.Concurrency/ConcurrentPriorityQueue.cs`
3. [REFACTOR] Aggressive-inline to match the `_subQueueMask` discipline (no division).

**Dependencies:** 5 · **Parallelizable:** No

### Task 7: [F2] Allocate bitmask + wire SubQueue index/reference
**Phase:** RED → GREEN → REFACTOR · **riskTier:** high · **boundaryTouching:** true · **Implements:** DR-1
1. [RED] `OccupancyBitmask_FreshQueue_AllWordsZero` (every sub-queue starts empty, mirroring
   `SubQueue` initial `EmptyFlag=1`).
   - File: `src/Bifrost.Tests.Concurrency/OccupancyBitmaskTests.cs`
   - Expected failure: `_occupancy` not allocated; `SubQueue` has no `_index`/`_occupancy`.
2. [GREEN] Allocate `_occupancy` in the core ctor (`ConcurrentPriorityQueue.cs:228`); add readonly
   `_index` + `_occupancy` fields to `SubQueue`; pass `(i, _occupancy)` at construction.
   - Files: `src/Bifrost.Concurrency/ConcurrentPriorityQueue.cs`, `src/Bifrost.Concurrency/SubQueue.cs`
3. [REFACTOR] Add internal `DebugOccupancyForTest` snapshot accessor.

**Dependencies:** 6 · **Parallelizable:** No

---

## Group T — Transition writes (DR-2) · sequential (all touch `SubQueue.cs`)

### Task 8: [T1] Set bit on empty→non-empty push
**Phase:** RED → GREEN → REFACTOR · **riskTier:** high · **boundaryTouching:** true · **Implements:** DR-2
1. [RED] `TryLockedPush_FirstItemIntoEmptySubQueue_SetsOccupancyBit`
   - File: `src/Bifrost.Tests.Concurrency/OccupancyBitmaskTests.cs`
   - Expected failure: no bitmask write on push.
2. [GREEN] In `TryLockedPush` (`SubQueue.cs:560`), when `wasEmpty`, after `HeapPush`, under the held
   lock: `Interlocked.Or(ref _occupancy[_index>>6], 1UL<<(_index&63))`.
3. [REFACTOR] Co-locate with the seqlock publish; one comment block.

**Dependencies:** 7 · **Parallelizable:** No

### Task 9: [T2] Clear bit on non-empty→empty pop
**Phase:** RED → GREEN → REFACTOR · **riskTier:** high · **boundaryTouching:** true · **Implements:** DR-2
1. [RED] `PopHeldRoot_LastItemRemoved_ClearsOccupancyBit` (and via `TryLockedPop`)
   - File: `src/Bifrost.Tests.Concurrency/OccupancyBitmaskTests.cs`
   - Expected failure: bit stays set after the queue drains.
2. [GREEN] In `PopHeldRoot` (`SubQueue.cs:639`), when `_size==0` after pop, under the held lock:
   `Interlocked.And(ref _occupancy[_index>>6], ~(1UL<<(_index&63)))`.
3. [REFACTOR] Ensure `TryLockedPop` inherits it (funnels through `PopHeldRoot`).

**Dependencies:** 8 · **Parallelizable:** No

### Task 10: [T3] Clear bit on LockedClear
**Phase:** RED → GREEN → REFACTOR · **riskTier:** high · **boundaryTouching:** true · **Implements:** DR-2
1. [RED] `LockedClear_NonEmptySubQueue_ClearsOccupancyBit`
   - File: `src/Bifrost.Tests.Concurrency/OccupancyBitmaskTests.cs`
   - Expected failure: bit stays set after `LockedClear`.
2. [GREEN] In `LockedClear` (`SubQueue.cs:675`), clear the bit alongside the empty publish.

**Dependencies:** 9 · **Parallelizable:** No

### Task 11: [T4] No write on non-boundary mutations
**Phase:** RED → GREEN → REFACTOR · **riskTier:** medium · **Implements:** DR-2
1. [RED] `Occupancy_PushToPopulatedAndNonLastPop_PerformsNoBitmaskWrite` (instrumented write counter)
   - File: `src/Bifrost.Tests.Concurrency/OccupancyBitmaskTests.cs`
   - Expected failure: write counter increments on non-boundary ops.
2. [GREEN] Confirm the `wasEmpty`/`_size==0` guards gate the writes; add a test-only
   `DebugOccupancyWriteCountForTest`.

**Dependencies:** 10 · **Parallelizable:** No

---

## Group R — Sparse routing (DR-3) · sequential (all touch `ConcurrentPriorityQueue.Dequeue.cs`)

### Task 12: [R1] Route to the single populated sub-queue without an O(n) scan
**Phase:** RED → GREEN → REFACTOR · **riskTier:** high · **boundaryTouching:** true · **Implements:** DR-3
Implements the design's **Sparse-fallback routing (Approach A)** requirement and the **Dequeue
control flow (Approach A)** section (Phase 1.5).
1. [RED] `TryDequeue_OneItemAfterSamplingMiss_RoutesViaBitmaskWithoutFullScan`
   - File: `src/Bifrost.Tests.Concurrency/SparseRoutingTests.cs`
   - Expected failure: no routing phase; falls to scan.
2. [GREEN] Insert Phase 1.5 between the sampling loop and the scan (`Dequeue.cs:160`): read
   `_occupancy`, iterate set bits via `TrailingZeroCount`, `TryPopFrom(index)`; `Success` returns.
3. [REFACTOR] Add a test-only routing-hit counter.

**Dependencies:** 11 · **Parallelizable:** No

### Task 13: [R2] Genuinely-empty → scan authority preserves the false contract
**Phase:** RED → GREEN → REFACTOR · **riskTier:** high · **boundaryTouching:** true · **Implements:** DR-3
1. [RED] `TryDequeue_EmptyQueue_RoutingFindsNothing_VerificationScanReturnsFalse`
   - File: `src/Bifrost.Tests.Concurrency/SparseRoutingTests.cs`
   - Expected failure: routing returns false directly (must defer to the scan).
2. [GREEN] When no bits are set, fall through to `TryDequeueVerificationScan` (unchanged authority).

**Dependencies:** 12 · **Parallelizable:** No

### Task 14: [R3] Dense path never enters routing
**Phase:** RED → GREEN → REFACTOR · **riskTier:** medium · **Implements:** DR-3
1. [RED] `TryDequeue_SamplingSucceeds_DoesNotEnterRoutingPhase` (routing-hit counter stays 0)
   - File: `src/Bifrost.Tests.Concurrency/SparseRoutingTests.cs`
   - Expected failure: counter increments / branch entered.
2. [GREEN] Confirm early-return on sampling `Success` precedes Phase 1.5.

**Dependencies:** 13 · **Parallelizable:** No

---

## Group V — Staleness & contract (DR-4)

### Task 15: [V1] Stale-set bit is safe
**Phase:** RED → GREEN → REFACTOR · **riskTier:** high · **boundaryTouching:** true · **Implements:** DR-4
1. [RED] `Routing_StaleSetBitOverEmptySubQueue_FallsThroughSafely` (force a set bit over an empty
   sub-queue via a test seam; routing must get `Empty` and continue/scan, never a wrong result).
   - File: `src/Bifrost.Tests.Concurrency/SparseRoutingTests.cs`
   - Expected failure: routing trusts the bit and returns a bad result.
2. [GREEN] Ensure `TryPopFrom` `Empty`/`Contended` skips the bit and continues; no path treats the
   bit as proof of an element.

**Dependencies:** 14 · **Parallelizable:** Yes (with Task 16)

### Task 16: [V2] n=1 collapse identical to today
**Phase:** RED → GREEN → REFACTOR · **riskTier:** medium · **Implements:** DR-4
1. [RED] `TryDequeue_SingleSubQueue_ReturnsExactMinAndHonestEmpty`
   - File: `src/Bifrost.Tests.Concurrency/OccupancyBitmaskTests.cs`
   - Expected failure: regression in the collapsed case.
2. [GREEN] Confirm the one-word bitmask mirrors the single `EmptyFlag`; exact-min path
   (`Dequeue.cs:78`) unaffected.

**Dependencies:** 7 · **Parallelizable:** Yes

### Task 17: [V3] Churn-near-empty conservation stress (Release)
**Phase:** RED → GREEN → REFACTOR · **riskTier:** high · **boundaryTouching:** true · **propertyTests:** true · **Implements:** DR-4
1. [RED] `ChurnNearEmpty_SingleItemInsertDrainStorm_NoFalseEmptyNoLostElement` (≥10⁶ ops, many
   producers/drainers); assert conservation + no `false` while an element was provably resident.
   - Files: `src/Bifrost.Tests.Concurrency/ConservationStressTests.cs` (extend),
     `src/Bifrost.Tests.Concurrency/EmptySemanticTests.cs`
   - Expected failure: surfaces any staleness bug under contention.
2. [GREEN] Fix any ordering defect uncovered.
3. [REFACTOR] Run in Release per the project lesson (Debug-green ≠ Release-green for async/concurrency).

**Dependencies:** 15 · **Parallelizable:** No (correctness authority; precedes benchmarks)

---

## Group P — Performance & before/after benchmarks (DR-5, DR-7)

### Task 18: [P1] Baseline capture on `main` (reproduce the problem)
**Phase:** Measurement · **riskTier:** low · **benchmarks:** true · **Implements:** DR-7
- On the pre-change tip: population sweep `{10,100,1k,100k,1M}` (latency), drain/sparse-contended
  throughput @ 2/8/32/64T, and dense UniformMixed/NarrowKey 1→64T. Population 10 must reproduce the
  ~260 ns sparse pair (problem demonstrated). Commit raw CSVs under `docs/benchmarks/data/<run>/`.
- Files: `src/Bifrost.Benchmarks/Concurrency/CpqSingleThreadedLatencyBenchmarks.cs`,
  `src/Bifrost.Benchmarks/Concurrency/ThroughputSweep.cs` (reuse).

**Dependencies:** 5 · **Parallelizable:** Yes (independent of prod code; runs on baseline)

### Task 19: [P2] Add sparse/drain benchmark + after capture
**Phase:** Measurement · **riskTier:** low · **benchmarks:** true · **Implements:** DR-7
- Add a drain-dominated sparse benchmark (queue hovering near empty) recording throughput and the
  scan-fall-through rate; run the full Task-18 matrix on the change (after).
- File: `src/Bifrost.Benchmarks/Concurrency/CpqSparseRoutingBenchmarks.cs` (new).

**Dependencies:** 17 · **Parallelizable:** No

### Task 20: [P3] Dense non-regression + zero-alloc gate (before vs after)
**Phase:** Measurement · **riskTier:** low · **benchmarks:** true · **Implements:** DR-5, DR-7
- Assert dense throughput within ±2% at every thread count and `0 B/op` preserved
  (`[MemoryDiagnoser]`), before vs after.

**Dependencies:** 19 · **Parallelizable:** No

### Task 21: [P4] False-sharing measurement (padding decision)
**Phase:** Measurement · **riskTier:** low · **benchmarks:** true · **Implements:** DR-5
- Measure `_occupancy` cache-line behavior under churn-near-empty; if a coherence regression shows,
  pad the words (spike/measure-decided). Dense path must show no regression regardless.

**Dependencies:** 20 · **Parallelizable:** Yes

### Task 22: [P5] Before/after results doc + paired charts
**Phase:** Measurement · **riskTier:** low · **Implements:** DR-7
- Write `docs/benchmarks/2026-06-14-cpq-bitmask-before-after.md`: environment table, paired
  before/after charts (regenerable via the `generate_charts.py` pattern), one-line verdict per
  workload, raw CSVs under `docs/benchmarks/data/<run>/`.

**Dependencies:** 20, 21 · **Parallelizable:** No

---

## Group Z — Conditional: O(1)-empty (Approach B) · DEFERRED (spike returned B-NO-GO, DR-6)

> **DEFERRED — NOT IN SCOPE.** The discover spike returned **B-NO-GO**
> ([findings](../research/2026-06-14-cpq-bitmask-spike.md)): the double-read is non-linearizable and
> the sound generation-stamp reintroduces a global contended counter that erases the win. Tasks
> 23–24 below are recorded as deferred follow-ups and are **not implemented** in this feature.
> Approach A captures the entire sparse benefit. Revisit only if a future profile shows the
> genuinely-empty scan is a measured hot spot *and* a per-word/sharded generation scheme is shown
> linearizable.

### Task 23: [Z1] Snapshot/generation protocol for authoritative all-zero
**Phase:** RED → GREEN → REFACTOR · **riskTier:** high · **boundaryTouching:** true · **Implements:** DR-6
1. [RED] `OccupancySnapshot_StablyAllZero_LinearizesToEmpty` + the false-empty stress from Task 4
   promoted to a permanent test.
   - File: `src/Bifrost.Tests.Concurrency/EmptySemanticTests.cs`
2. [GREEN] Implement the proven protocol (double-read or generation stamp).

**Dependencies:** 4 (B-go), 17 · **Parallelizable:** No · **Conditional:** Yes

### Task 24: [Z2] O(1)-empty in TryDequeue + IsEmpty short-circuit
**Phase:** RED → GREEN → REFACTOR · **riskTier:** high · **boundaryTouching:** true · **Implements:** DR-6
1. [RED] `TryDequeue_EmptyQueue_ReturnsFalseInO1WithoutFullScan` and
   `IsEmpty_AllZeroOccupancy_ShortCircuits`
   - Files: `src/Bifrost.Tests.Concurrency/EmptySemanticTests.cs`,
     `src/Bifrost.Concurrency/ConcurrentPriorityQueue.Count.cs`
2. [GREEN] Use the Task-23 protocol; retain the verification scan as fallback unless the spike
   explicitly cleared its removal.

**Dependencies:** 23 · **Parallelizable:** No · **Conditional:** Yes

---

## Parallelization

```
1 ─┬─ 2 ─┐
   ├─ 3 ─┼─ 5 ─┬─ 6 ─ 7 ─ 8 ─ 9 ─ 10 ─ 11 ─ 12 ─ 13 ─ 14 ─ 15 ─┬─ 17 ─ 19 ─ 20 ─┬─ 21 ─ 22
   └─ 4 ─┘     │                                            16 ─┘                 │
              └─ 18 (baseline, parallel — runs on pre-change tip) ────────────────┘
              └─ (23 ─ 24)  ONLY if Task 4 = B-go
```

- **Sequential chains:** Group T (Tasks 8–11, all touch `SubQueue.cs`) and Group R (Tasks 12–14,
  all touch `Dequeue.cs`) must serialize within-group.
- **Parallel-safe:** Tasks 2/3/4 concurrently; Task 18 (baseline) independent of the production
  chain; 15∥16; 21 parallel after 20.
- **Conditional:** Tasks 23–24 gated on the spike B-verdict.

## Risks

- **Spike returns dense-regression (Task 2 fail):** the "free when dense" premise breaks → revisit
  representation (padding, per-stripe occupancy) before committing Groups T/R. Human checkpoint.
- **Release-only staleness flake (Task 17):** mandatory Release runs; the FakeTimeProvider/Release
  lesson applies — do not declare green on Debug alone.
- **Benchmark host variance:** before/after must run on the same host (the Xeon 8573C harness) for
  the ±2% gate to be meaningful.
