# Implementation Plan: 0.5.0 Release-Hardening Sweep

**Design:** `docs/designs/2026-06-14-release-hardening-sweep.md`
**Feature ID:** `release-hardening-sweep`
**Bundle:** #19, #20, #21, #24
**Date:** 2026-06-14

## Overview

17 tasks across five groups, mapped to the design's five requirements. Groups A
(hygiene) and C (coverage tests) are highly parallel; Group B (queue disposal) is
mostly sequential on two shared files; Group D (per-project gate) is sequenced **after**
Group C so the CI gate never reds `main`.

**Traceability summary** (full matrix: `…-traceability.md`):

| DR | Requirement | Tasks |
|----|-------------|-------|
| DR-1 | Pin Actions to SHAs (#19) | 1 |
| DR-2 | Bump OpenTelemetry.Api (#20) | 2 |
| DR-3 | Dispose queue resources + trim per-wait alloc (#21) | 3, 4, 5, 6 |
| DR-4 | Raise Bifrost.Concurrency to ≥80% (#24) | 7–14 |
| DR-5 | Per-project coverage gate (#24) | 15, 16, 17 |

## Note on TDD shape

Tasks **3–6** (DR-3) and **15–17** (DR-5) introduce/change production code → strict
RED→GREEN→REFACTOR (failing test first). Tasks **7–14** (DR-4) are **coverage back-fill
against code that already shipped in #18** — they are test-only (no production change),
so they are `[TEST]` tasks: each must still *fail-if-deleted* (assert real behavior, not
tautologies) and `await` its assertions (TUnit). Tasks **1–2** (DR-1/DR-2) change no
production code; their verification is a guard assertion + the existing suite as
regression net (`[VERIFY]`).

---

## Group A — Hygiene / config (parallel-safe, independent)

### Task 1: Pin all GitHub Actions to commit SHAs + Dependabot (DR-1, #19)
**Phase:** VERIFY

1. [VERIFY-RED] Add a guard assertion that fails on the current tree:
   - `grep -rEn 'uses:[[:space:]]*[^@]+@v[0-9]+' .github/workflows/` must return **no
     matches**. Today it matches `ci.yml`, `publish.yml`, `project-automation.yml` →
     fails (the RED state).
2. [GREEN] Rewrite every `uses:` in `ci.yml`, `publish.yml`, `project-automation.yml`
   to a 40-char commit SHA annotated `# vN` (mirror `soak.yml`'s existing pins):
   `actions/checkout`, `actions/setup-dotnet`, `actions/upload-artifact`. Resolve each
   SHA to the tip of the action's current major tag.
3. [GREEN] Add `.github/dependabot.yml` (`package-ecosystem: github-actions`, weekly) —
   open design Q1 resolved **yes** (keeps pins fresh).
4. [VERIFY] Guard grep returns no matches; a CI run on the branch stays green.

**testingStrategy:** verify-only (no unit). **propertyTests:** no. **benchmarks:** no.
**Dependencies:** None. **Parallelizable:** Yes.

### Task 2: Bump OpenTelemetry.Api off CVE-2026-40894 (DR-2, #20)
**Phase:** VERIFY

1. [VERIFY-RED] `dotnet restore src/Bifrost.sln` currently emits **NU1902** for
   `Bifrost.OpenTelemetry` + `Bifrost.Tests` (the RED state).
2. [GREEN] In `src/Directory.Packages.props` only, bump `OpenTelemetry.Api`
   `1.14.0` → **`1.15.3`** (first patched version; verify it is the latest stable ≥1.15.3
   at implementation time). No version edit may leak into any `.csproj`.
3. [VERIFY] `dotnet build src/Bifrost.sln -c Release` emits **no NU1902**; the OpenTelemetry
   integration tests (`Bifrost.Tests` OTel suite) pass; no API-break compile errors across
   the 1.14→1.15 line.

**testingStrategy:** verify-only (existing OTel tests = regression net).
**propertyTests:** no. **benchmarks:** no. **Dependencies:** None. **Parallelizable:** Yes.

---

## Group B — DR-3 queue disposal + alloc trim (#21)

Tasks 3 and 4 touch different files (parallel); Task 5 depends on both; Task 6 edits the
same two binding files as 3/4, so it runs **after** 3/4 (or in the same worktree) to
avoid merge conflict. DR-3 tests live in **`src/Bifrost.Tests`** (bindings are `internal`
to `Bifrost`, already `InternalsVisibleTo` that suite).

### Task 3: ConcurrentPriorityWorkQueue disposes its SemaphoreSlim (DR-3)
**Phase:** RED → GREEN → REFACTOR

1. [RED] Write tests:
   - `ConcurrentPriorityWorkQueue_Dispose_ThenWait_ThrowsObjectDisposedException`
   - `ConcurrentPriorityWorkQueue_DisposeTwice_DoesNotThrow` (idempotency / edge case)
   - File: `src/Bifrost.Tests/Queues/ConcurrentPriorityWorkQueueDisposalTests.cs`
   - Expected failure: type does not implement `IDisposable` → no `Dispose()` to call
     (compile/red).
2. [GREEN] `ConcurrentPriorityWorkQueue<TWork> : IWorkQueue<…>, IDisposable`; `Dispose()`
   disposes `_signal`, guarded by an `Interlocked.Exchange` idempotency flag.
3. [REFACTOR] Confirm **no CA2213** finding remains on the binding (warnings-as-errors).

**testingStrategy:** unit + analyzer-gate. **propertyTests:** no. **benchmarks:** no.
**Dependencies:** None. **Parallelizable:** Yes (with Task 4).

### Task 4: LockingPriorityWorkQueue disposes its SemaphoreSlim (DR-3)
**Phase:** RED → GREEN → REFACTOR

1. [RED] Mirror Task 3's two tests in
   `src/Bifrost.Tests/Queues/LockingPriorityWorkQueueDisposalTests.cs`.
   Expected failure: type does not implement `IDisposable`.
2. [GREEN] `LockingPriorityWorkQueue<TWork> : …, IDisposable`; dispose `_signal`,
   idempotent.
3. [REFACTOR] No CA2213 on the binding.

**testingStrategy:** unit + analyzer-gate. **Dependencies:** None.
**Parallelizable:** Yes (with Task 3).

### Task 5: WorkOrchestrator.DisposeAsync disposes the owned queue (DR-3)
**Phase:** RED → GREEN → REFACTOR

1. [RED] Write test:
   - `WorkOrchestrator_DisposeAsync_DisposesPriorityBinding_SubsequentWaitThrows`
   - File: `src/Bifrost.Tests/Orchestrator/WorkOrchestratorDisposalTests.cs`
   - Build an orchestrator with `DispatchStrategy.PriorityMultiQueue`, `DisposeAsync()`,
     then assert the binding exposed via the internal `WorkQueue` property is disposed
     (its `WaitToDequeueAsync` throws `ObjectDisposedException`).
   - Expected failure: `DisposeAsync` (line 354) never disposes `_queue`.
2. [GREEN] In `WorkOrchestrator.DisposeAsync`, after worker drain, dispose `_queue`:
   `if (_queue is IAsyncDisposable ad) await ad.DisposeAsync(); else if (_queue is
   IDisposable d) d.Dispose();`. `FifoChannelWorkQueue` (non-disposable) is unaffected.
3. [REFACTOR] Verify idempotent double-`DisposeAsync` of the orchestrator stays safe.

**testingStrategy:** unit. **propertyTests:** no. **benchmarks:** no.
**Dependencies:** Tasks 3, 4. **Parallelizable:** No.

### Task 6: Trim the per-wait allocation in WaitToDequeueAsync (DR-3)
**Phase:** RED(bench) → GREEN → REFACTOR

1. [RED] Add `PriorityWaitAllocationBenchmarks` to
   `src/Bifrost.Benchmarks/Allocation/` (`[MemoryDiagnoser]`) exercising the park→wake
   path of `ConcurrentPriorityWorkQueue.WaitToDequeueAsync`; record the current per-wait
   allocation (>0 B from the `async ValueTask<bool>` state-machine box) as the baseline.
2. [GREEN] Eliminate/amortize the box: apply
   `[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<bool>))]` to the
   `WaitToDequeueAsync` methods of both priority bindings (the standard pooled-box trick),
   or restructure so the park path reuses a cached awaitable. Keep AOT-safe.
3. [REFACTOR] Re-run the benchmark: steady-state park-path allocation is **0 B** (or a
   documented reduction); the completion-handshake semantics (waiter-count / fence) are
   unchanged — re-run the existing binding concurrency tests.

**testingStrategy:** benchmark + existing concurrency regression.
**propertyTests:** no. **benchmarks:** **yes**. **Dependencies:** Tasks 3, 4.
**Parallelizable:** No (edits the same two binding files).

---

## Group C — DR-4 Bifrost.Concurrency coverage to ≥80% (#24)

Tasks 7–13 are test-only additions to **`src/Bifrost.Tests.Concurrency`**, one file each
→ fully parallel. Task 14 is the gating verification (depends on 7–13). Targets and the
#18 defensive branches come straight from issue #24.

### Task 7: Strict-min path — Inspect.cs (DR-4)
**Phase:** TEST

1. [TEST] `src/Bifrost.Tests.Concurrency/StrictMinInspectTests.cs` covering
   `TryDequeueMin`, `TryScanForMinimum`, `TryScanRunnerUp`:
   - `TryDequeueMin_SingleThreaded_ReturnsTrueGlobalMinimum`
   - `TryDequeueMin_UnderContention_RevalidatesAndLosesNoElement` (stress; vs relaxed path)
   - `TryDequeueMin_EmptyQueue_ReturnsFalse`
   - `TryDequeueMin_PopHeldRoot_RemovesUnderHeldLock` (the #18 pop-under-held-lock fix)

**testingStrategy:** unit + property/stress. **propertyTests:** **yes** (contention/no-loss).
**benchmarks:** no. **Dependencies:** None. **Parallelizable:** Yes.

### Task 8: Collection surface — Collection.cs (DR-4)
**Phase:** TEST

1. [TEST] `src/Bifrost.Tests.Concurrency/CollectionSurfaceTests.cs`:
   - `ToArray_SnapshotsContentsAndSize`
   - `ToArray_UnderConcurrentMutation_IsWeaklyConsistent`
   - `Clear_ReleasesReferences_AndEmptiesQueue`
   - `ToArray_LongAccumulateOverflow_ClampsCount` (#18 `long`-accumulate clamp — see
     Task 14 risk note if unreachable without absurd element counts)
   - unordered-enumeration snapshot

**testingStrategy:** unit + property. **propertyTests:** **yes** (weak consistency).
**Dependencies:** None. **Parallelizable:** Yes.

### Task 9: Bounded-capacity edges — ConcurrentPriorityQueue.cs / .Enqueue.cs (DR-4)
**Phase:** TEST

1. [TEST] `src/Bifrost.Tests.Concurrency/BoundedCapacityEdgeTests.cs`:
   - `Ctor_BoundedCapacityZero_Throws` / `Ctor_BoundedCapacityBelowMinusOne_Throws`
     (#18 internal-ctor validation)
   - `Enqueue_WhenBoundedAndFull_ThrowsInvalidOperationException`
   - `Enqueue_ThrowingComparer_RollsBackReservation` (#18 reservation rollback `catch`,
     driven by a throwing comparer — error-path coverage)
   - `BoundedCount_ConservedAcrossRejection`

**testingStrategy:** unit (error paths). **propertyTests:** no. **Dependencies:** None.
**Parallelizable:** Yes.

### Task 10: SubQueue edges — SubQueue.cs (DR-4)
**Phase:** TEST

1. [TEST] `src/Bifrost.Tests.Concurrency/SubQueueEdgeTests.cs`:
   - `Grow_PastArrayMaxLength_Clamps` + `Grow_CannotGrowFurther_Throws` (#18 clamp/throw;
     see Task 14 risk note)
   - `SnapshotTo_CopiesLockedContents`
   - `LockedClear_EmptiesAndResets`
   - seqlock tear/retry edge under concurrent writer

**testingStrategy:** unit + property/stress. **propertyTests:** **yes** (seqlock tear).
**Dependencies:** None. **Parallelizable:** Yes.

### Task 11: Comparer normalization — PriorityComparerHelpers.cs (DR-4)
**Phase:** TEST

1. [TEST] `src/Bifrost.Tests.Concurrency/PriorityComparerHelpersTests.cs`:
   custom-vs-default comparer normalization branches (default `Comparer<T>.Default`
   passthrough; custom comparer wrapping; the 33% branch gap).

**testingStrategy:** unit. **propertyTests:** no. **Dependencies:** None.
**Parallelizable:** Yes.

### Task 12: Stickiness + ThreadHandle (DR-4)
**Phase:** TEST

1. [TEST] `src/Bifrost.Tests.Concurrency/StickinessTests.cs`:
   - `Ctor_StickinessNegativeBelowDefault_Throws` / `-1` default accepted
   - `ThreadHandle_StickyResetsOnContention` (sticky sub-queue abandoned on a contended pop)

**testingStrategy:** unit + property. **propertyTests:** **yes** (contention reset).
**Dependencies:** None. **Parallelizable:** Yes.

### Task 13: DebugView (DR-4, low priority — only if needed to clear 80%)
**Phase:** TEST

1. [TEST] `src/Bifrost.Tests.Concurrency/DebugViewTests.cs` — exercise
   `ConcurrentPriorityQueueDebugView` (cheap; include only if Task 14 shows the package
   still short of 80% without it).

**testingStrategy:** unit. **Dependencies:** None. **Parallelizable:** Yes.

### Task 14: Coverage verification + uncoverable-branch disposition (DR-4)
**Phase:** VERIFY

1. [VERIFY] Run in isolation:
   `dotnet run --project src/Bifrost.Tests.Concurrency -c Release -- --coverage
   --coverage-output-format cobertura --coverage-output <out>` then
   `scripts/ci/coverage-gate.sh --coverage-file <out> --threshold 80`.
2. Confirm **≥80% line AND ≥80% branch**. If short, add targeted tests (incl. Task 13).
3. For #18 defensive clamps genuinely unreachable without absurd allocation
   (`ToArray` `long`-overflow, `SubQueue.Grow` `Array.MaxLength`): either add a
   testability seam (internal method exercised directly) **or** annotate
   `[ExcludeFromCodeCoverage]` with a justification comment — decide per branch, document
   in the PR. (Note: current `coverage-gate.sh` gates line only; **branch** must be checked
   manually here until Task 15 lands.)

**testingStrategy:** verify-only. **propertyTests:** no. **benchmarks:** no.
**Dependencies:** Tasks 7–13. **Parallelizable:** No.

---

## Group D — DR-5 per-project coverage gate (#24)

Sequenced **after** Group C. Tasks 15/16 edit the same script → sequential. Task 17 (CI
flip) depends on Task 14 proving Concurrency ≥80%, so `main` never reds.

### Task 15: Gate branch coverage, not just line (DR-5)
**Phase:** RED → GREEN

1. [RED] `scripts/ci/coverage-gate.test.sh` (bash harness; add `bats`-style asserts or
   plain exit-code checks). Fixture: `scripts/ci/testdata/line85-branch70.cobertura.xml`
   (line-rate 0.85, branch-rate 0.70). Assert the gate **exits 1**.
   Expected failure: current script gates line only → exits 0 (RED).
2. [GREEN] Add branch-coverage gating to `coverage-gate.sh`: fail when branch% < threshold
   as well as line%. Fixture `line85-branch85` → exit 0.

**testingStrategy:** script-fixture. **propertyTests:** no. **benchmarks:** no.
**Dependencies:** None (script work; can start alongside Group C).
**Parallelizable:** Yes (vs Group C), but **before** Task 16.

### Task 16: Per-project gating mode (DR-5)
**Phase:** RED → GREEN

1. [RED] Extend `coverage-gate.test.sh`: fixture dir with two project cobertura files
   (`A` 0.90/0.90, `B` 0.60/0.55). Assert the gate **fails** because `B` < 80%.
   Expected failure: no per-project mode exists (RED).
2. [GREEN] Add `--per-project <dir>` (gate each `*.cobertura.xml` independently; fail if
   **any** project < threshold line or branch; print a per-project pass/fail table for the
   PR comment). Open design Q2 resolved: scope = **shipping-package suites** (exclude pure
   test-support projects) — encode the includelist/excludelist.

**testingStrategy:** script-fixture. **Dependencies:** Task 15. **Parallelizable:** No.

### Task 17: Wire CI to per-project gating (DR-5)
**Phase:** GREEN → VERIFY

1. [GREEN] Update `.github/workflows/ci.yml` `coverage-gate` job to invoke
   `coverage-gate.sh --per-project ./TestResults` (per-project cobertura already emitted
   at `ci.yml:52`) instead of gating only the merged `Cobertura.xml`; surface the
   per-project table in the PR comment.
2. [VERIFY] Sequencing guard: this lands **with/after** Task 14 — confirm every shipping
   suite (incl. `Bifrost.Tests.Concurrency`, now ≥80%) passes the per-project gate so the
   first CI run on the flip is green.

**testingStrategy:** verify-only (CI run). **propertyTests:** no. **benchmarks:** no.
**Dependencies:** Tasks 14, 16. **Parallelizable:** No.

---

## Parallelization & sequencing

```
Group A:  Task 1 ─┐   Task 2 ─┐         (independent, parallel)
Group B:  Task 3 ─┴─ Task 4 ──┴─→ Task 5 → Task 6
Group C:  Task 7 … Task 13  (parallel) ──→ Task 14
Group D:  Task 15 → Task 16 ─────────────→ Task 17
                                     ↑           ↑
                              (Task 14 gate) ─────┘  (Concurrency ≥80% before CI flip)
```

- **Worktree-parallel groups:** {1}, {2}, {3,4}, {7,8,9,10,11,12,13}, {15→16}.
- **Cross-group barrier:** Task 17 must not merge before Task 14 (the design's DR-5
  sequencing guard — flipping the gate while Concurrency < 80% reds `main`).

## Risks

- **Uncoverable defensive clamps** (Task 14): `ToArray` overflow / `SubQueue.Grow`
  `Array.MaxLength` may be unreachable without absurd allocation → seam-or-exclude
  decision, documented per branch. Acceptance is ≥80%, not 100%, so a justified
  `[ExcludeFromCodeCoverage]` is acceptable.
- **#24 dominates effort** (Group C, 7 test files) → mitigated by full file-level
  parallelism; Task 17 gated behind it so a partial Group C can't red `main`.
- **Pooled async builder + AOT** (Task 6): verify `PoolingAsyncValueTaskMethodBuilder`
  stays trim/AOT-safe (IsAotCompatible projects) — re-run the AOT smoke if in doubt.

## Scope expansion (2026-06-15) — DR-5 re-based to per-assembly + DR-6

Pre-flight measurement before wiring CI (task-17) revealed that gating per-*test-project*
cobertura file would red `main`: those files blend **incidental** coverage of large shared
libs (the scheduling suite loads all of `Bifrost.Concurrency` but tests little of it → 59%).
User decision: **re-base the gate on per-owned-assembly coverage** (the meaningful unit) and
close the genuine gaps. Measured per-assembly (merged across suites), only two shipping
assemblies miss ≥80% **branch** (all others pass line+branch):

| Assembly | line | branch | gap |
|----------|------|--------|-----|
| `Bifrost.Core` | 92.8% | **50.0% (2/4)** | 1 arm: `PriorityDispatchOptions.cs:200` `double.IsNaN` |
| `Bifrost.Resilience` | 92.0% | **77.8% (56/72)** | 4 arms: `ResiliencyPolicyGenerator.cs:236/245/263/267` |

### DR-5 (re-based) — task-18: per-assembly gate
Add a `--per-assembly <merged-cobertura.xml>` mode to `coverage-gate.sh` that gates **each
`<package>` (shipping assembly)** in the merged report at ≥80% line AND branch (the
package-level `branch-rate` attrs are reliable — verified against summed condition counts),
failing if any shipping assembly is below; per-assembly table to the PR comment.
RED fixture (one package <80%) → GREEN. Keeps the per-file `--per-project` mode.

### DR-6 — task-19: Bifrost.Core branch ≥80%
Cover `PriorityDispatchOptions.ThrowIfNotInUnitInterval` NaN arm — assign a `double.NaN`
watermark (Batch/Default/Interactive) and assert `ArgumentOutOfRangeException`. 2/4 → 4/4.
Test in `src/Bifrost.Tests`.

### DR-6 — task-20: Bifrost.Resilience branch ≥80%
Cover the four `ResiliencyPolicyGenerator` arms: exponential-vs-fixed backoff (236),
negative-delay clamp (245, note `Random.Shared` jitter — use a seam or test the private
static directly via reflection in the test project), final-vs-non-final retry log level
(263) + message (267). 56/72 → ≥80%. Test in `src/Bifrost.Tests`.

### task-17 (revised)
Wire `.github/workflows/ci.yml` coverage-gate job to `--per-assembly` on the merged report;
verify all 9 shipping assemblies pass. Depends on task-18 + task-19 + task-20.

## Open design questions — resolutions (confirm at plan-review)

1. **#19 Dependabot** → **adopt** (`.github/dependabot.yml`, Task 1) — recommended, cheap.
2. **DR-5 project scope** → **shipping-package suites only** (Task 16) — exclude pure
   test-support projects.
3. **PR shape** → **single hardening PR** (one review unit, matches the bundle theme);
   revisit if Group C balloons.
