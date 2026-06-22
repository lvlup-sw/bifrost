# Implementation Plan — Milestone v0.5.1 (refactor)

**Feature ID:** `refactor-v051-milestone`
**Track:** overhaul (single combined PR)
**Issues:** [#46](https://github.com/lvlup-sw/bifrost/issues/46) (CPQ dispatch), [#32](https://github.com/lvlup-sw/bifrost/issues/32) (scheduling follow-ups)
**Date:** 2026-06-22

## Summary

Two independent, deferred concerns landed as one overhaul PR (user decision):

- **#46** — `PriorityBinding.Auto` must never silently resolve to the coarse-locking heap. `Locking` becomes explicit opt-in only; the capacity/rank-error heuristic is removed.
- **#32** — 10 review follow-ups deferred from PR #25: 5 test-hygiene fixes, 4 production-hardening items (verify-then-fix), 1 design-doc gap.

## Invariant constraints (enforced at delegate/review via `check_invariant_conformance`)

- **U-4 (dispatch-strategy)** — dispatch strategies / priority bindings selected via closed enums + a construction-time factory switch; bindings stay `internal sealed`; no reflection/codegen. Governs **Task 1**. The change keeps the enum + switch and only deletes the heuristic, so it stays conformant.
- **U-5 (failure-observability)** — every failed/rejected/shed item stays observable (routed, returned, or counted+logged). Governs **Tasks 3, 4**. Do NOT regress the documented publish-fault swallows (`JobDispatcherRouter.RunAsync`, `InFlightTrackingDispatcher` scope-dispose) — those are deliberate isolation seams with `CA1031` suppressions, not silent drops.

## Verification ladder

Each task is stamped with a `riskTier` that drives verification depth (test-after, not test-first):
- **low** → static analysis only (build clean under warnings-as-errors).
- **medium** → scoped tests + `check_test_adequacy` kill-probe.
- **high** → medium set + integration suite across the seam (`dotnet test --solution src/Bifrost.sln`).

**Test runner reminder:** TUnit on MTP — `dotnet test --project <proj>` / `--solution src/Bifrost.sln`, never bare `dotnet test`. Assertions are `await`ed.

**FakeTimeProvider gotcha (Task 3 backoff):** arm any timer/delay *before* the first `await`, drive time via the injected `TimeProvider`, and verify in **Release** — Debug-green ≠ Release-green for async timing. Use `[Property("Category","Stress")]` only if a proof needs real concurrency; per-PR CI gates stress out.

## File ownership (worktree conflict avoidance)

Each task owns a disjoint file set so parallel worktrees merge cleanly. Task 3 and Task 4 add **new** test files (they must not edit the five files Task 2 owns). No `.csproj` edits — SDK-style globbing picks up new files automatically.

---

## Workstream A — #46 CPQ dispatch

### Task 1: Auto never selects Locking; remove dead heuristic
**Risk Tier:** medium
**Boundary Touching:** false
**Goals:** G1, G2, G3, G4

**Changes:**
- `PriorityBindingResolver.cs`: `ResolveAuto` always returns `DispatchStrategy.PriorityMultiQueue`. Remove `RankErrorNumerator`, `CapacityThresholdMultiplier`, and `ResolveAuto`'s `processorCount`/`capacity` params. Simplify `Resolve` to `Resolve(PriorityBinding requested)` — Auto no longer needs capacity/processorCount. **Keep `SubQueueCountFor`** (still used by `WorkOrchestrator.cs:162` and mirrored by `ConcurrentPriorityQueue`). Rewrite the class-level `<remarks>` (drop the rank-error/threshold derivation; state Auto ⇒ MultiQueue, Locking is explicit).
- `WorkOrchestrator.cs:133`: update call site to the one-arg `Resolve(opts.Priority.Binding)`. Update binding-resolution XML docs (~L187-188/245) to describe explicit opt-in.
- `PriorityBinding.cs`: XML docs — `Auto` never selects locking; `Locking` is explicit opt-in for strict rank ordering at high capacity, noting the relaxed-dequeue rank-accuracy tradeoff.
- `PriorityDispatchExtensions.cs`: reflect explicit opt-in in binding docs.

**Verification (medium):** scoped tests + kill-probe.
- `PriorityBindingResolverTests.cs`: replace the Auto→Locking cases (current T4/T5 at L79/91/103/115) with a property/parametrized test `Resolve_Auto_AlwaysResolvesToMultiQueue` across capacities {32,128,1024,…} × processor counts {4,8,16,17,32}. Keep the explicit `Locking`/`MultiQueue` cases and the T6 `SubQueueCountFor` agreement test.
- `WorkOrchestratorBindingResolutionTests.cs`: update for the new `Resolve` signature; assert `LockingPriorityWorkQueue` is constructed **only** for an explicit `PriorityBinding.Locking` (and an `Auto` high-capacity case yields the MultiQueue-backed queue).

**Files:** `src/Bifrost/Queues/PriorityBindingResolver.cs`, `src/Bifrost/WorkOrchestrator.cs`, `src/Bifrost/DependencyInjection/PriorityDispatchExtensions.cs`, `src/Bifrost.Core/PriorityBinding.cs`, `src/Bifrost.Tests/Queues/PriorityBindingResolverTests.cs`, `src/Bifrost.Tests/WorkOrchestratorBindingResolutionTests.cs`
**Dependencies:** None
**Parallelizable:** Yes (disjoint from all #32 tasks)

---

## Workstream B — #32 scheduling follow-ups

### Task 2: Scheduler test-hygiene sweep (5 items)
**Risk Tier:** low
**Boundary Touching:** false
**Goals:** G5

**Changes (test-only):**
1. `Observability/SchedulerEventsPublicationTests.cs` (~L151, L195) — deterministically dispose each `SchedulerEventStream` (e.g. `await using` / try-finally) so no channel is retained across tests.
2. `Testing/AddSchedulerTestingTests.cs` (~L102) — move the hosted-service `StartAsync` loop *inside* the guarded cleanup scope so a startup failure still stops already-started services.
3. `TestThreadPoolInitializer.cs` (~L33) — check the `ThreadPool.SetMinThreads` return value; fail fast (throw) on configuration failure.
4. `TickEngine/ClockJumpTests.cs` — wrap the loop start/exercise in try/finally guaranteeing `StopAsync`/`Dispose` on early failure.
5. `TickEngine/TickLoopExceptionTests.cs` (~L151) — align `Fixture.Skew` nullability with the non-skew path; drop the `skew!` mask (model the no-skew case as genuinely nullable).

**Verification (low):** build clean under warnings-as-errors; the affected scheduling test files run green. No new production behavior.

**Files:** `src/Bifrost.Tests.Scheduling/Observability/SchedulerEventsPublicationTests.cs`, `src/Bifrost.Tests.Scheduling/Testing/AddSchedulerTestingTests.cs`, `src/Bifrost.Tests.Scheduling/TestThreadPoolInitializer.cs`, `src/Bifrost.Tests.Scheduling/TickEngine/ClockJumpTests.cs`, `src/Bifrost.Tests.Scheduling/TickEngine/TickLoopExceptionTests.cs`
**Dependencies:** None
**Parallelizable:** Yes

### Task 3: Tick-loop fire/fault hardening (verify-then-fix)
**Risk Tier:** high
**Boundary Touching:** true
**Goals:** G6a, G6b, G6d

**Changes — read the seam, prove the gap with a failing test, then fix; do not add speculative guards:**
- **G6a — in-flight double-decrement (CONFIRM, already mitigated):** `InFlightTrackingDispatcher` already has a single-shot completion guard (`Interlocked.Exchange(ref completed, 1)` at L1361/L1380) clearing in-flight exactly once across the pool-thread `finally` and `CompleteFromHandoffFault`. Add a regression test that drives a non-conforming router which both starts the dispatch and throws synchronously, and asserts in-flight returns to 0 exactly once (no underflow, idle barrier releases). If a test already covers this, document closure; do not change production code.
- **G6b — orphan `JobFiredEvent` on scope fault during shutdown (VERIFY-THEN-FIX):** confirm the per-fire `CreateScope` fault path (~ScheduleTickLoop L948-991) still emits a terminal event (`JobFireFailedEvent`) and clears in-flight, so a published `JobFiredEvent` is never left without a terminal during shutdown (U-5). Add a regression test forcing `CreateScope` to throw mid-shutdown. Fix only if a real gap (missing terminal event or stranded in-flight) is found.
- **G6d — fault-restart backoff (FIX, real gap):** the fault-recovery loop (L305-322) restarts immediately; `HandleTickLoopFault` only slides the restart-count window. Add a bounded backoff delay between restarts, driven by the injected `TimeProvider` and cancellable by the stopping token, so a crash loop does not spin CPU. Add a `SchedulerOptions` knob (e.g. `RestartBackoff`, defaulted) for the delay. Keep the existing `MaxRestartsInWindow` give-up behavior intact.

**Verification (high):** scoped tests + kill-probe + integration suite across the tick-loop seam.
- New test files only (do NOT touch Task 2's files): e.g. `TickEngine/FaultBackoffTests.cs`, and a new in-flight/scope-fault test file. Use `FakeTimeProvider`; assert backoff elapses via fake time, the loop re-arms, and cancellation during backoff exits cleanly.
- `dotnet test --solution src/Bifrost.sln` green in Release.

**Files:** `src/Bifrost.Scheduling/TickEngine/ScheduleTickLoop.cs`, `src/Bifrost.Scheduling/SchedulerOptions.cs`, new test files under `src/Bifrost.Tests.Scheduling/TickEngine/`
**Dependencies:** None (owns `ScheduleTickLoop.cs` + `SchedulerOptions.cs` exclusively)
**Parallelizable:** Yes

### Task 4: ScheduleRegistry disposes its command channel (G6c)
**Risk Tier:** medium
**Boundary Touching:** true
**Goals:** G6c

**Changes:**
- `ScheduleRegistry.cs`: implement `IAsyncDisposable` (or `IDisposable`) that calls `this.commands.Writer.Complete()` so the tick-loop reader drains and exits cleanly on shutdown. Make idempotent (guard double-dispose). Preserve the `sealed partial` shape.
- Verify (read-only) that the tick-loop command reader exits gracefully when the channel completes (`WaitToReadAsync` → false). If the reader needs a code change, that file is owned by Task 3 — **flag as a dependency rather than editing `ScheduleTickLoop.cs`** (escalate to orchestrator). Expectation: the reader already exits on completion, so no Task 3 edit is needed.

**Verification (medium):** scoped tests + kill-probe.
- New test file `src/Bifrost.Tests.Scheduling/Registry/ScheduleRegistryDisposeTests.cs`: assert dispose completes the writer, a pending reader observes completion, and double-dispose is safe.

**Files:** `src/Bifrost.Scheduling/Registry/ScheduleRegistry.cs`, new `src/Bifrost.Tests.Scheduling/Registry/ScheduleRegistryDisposeTests.cs`
**Dependencies:** None (soft: do not edit `ScheduleTickLoop.cs`)
**Parallelizable:** Yes

### Task 5: Document `UpdateAsync` in the design doc (G7)
**Risk Tier:** low
**Boundary Touching:** false
**Goals:** G7

**Changes:** add `UpdateAsync(name, cadence, missedFirePolicy, ct)` to the Type-model / registry-operations section of `docs/designs/2026-04-10-durable-scheduling-api.md` alongside Register/Unregister/Pause/Resume/Trigger, matching the implemented semantics (cadence update, past-date validation, tick-loop re-arm).

**Verification (low):** doc-link/consistency check; no code change.

**Files:** `docs/designs/2026-04-10-durable-scheduling-api.md`
**Dependencies:** None
**Parallelizable:** Yes

---

## Parallelization

All five tasks are parallel-safe under the file-ownership boundaries above — a single fan-out of 5 worktrees.

| Group | Tasks | Notes |
|-------|-------|-------|
| 1 (parallel) | Task 1, Task 2, Task 3, Task 4, Task 5 | Disjoint file sets; Task 3/4 add new test files only |

## Traceability

| Goal | Issue | Task |
|------|-------|------|
| G1, G2, G3, G4 | #46 | Task 1 |
| G5 | #32 | Task 2 |
| G6a, G6b, G6d | #32 | Task 3 |
| G6c | #32 | Task 4 |
| G7 | #32 | Task 5 |

## Out of scope

- Splitting the milestone into two PRs (user chose one combined PR).
- Re-adding any Auto→Locking heuristic (#46 explicitly removes it; documented tradeoff stays in XML docs only).
- New scheduler features beyond the deferred #32 list.
