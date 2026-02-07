# Implementation Plan: Benchmark Improvements

## Source Design
Brief: `docs/workflow-state/refactor-benchmark-improvements.state.json`
Benchmark results: `docs/benchmarks/2026-02-06-full-suite-results.md`

## Scope
**Target:** All three "Future Improvements" from v0.2.0 benchmark results
**Excluded:** None

## Summary
- Total tasks: 5
- Parallel groups: 2
- Estimated test count: 8 new/modified tests
- Design coverage: 3 of 3 improvements covered

## Spec Traceability

### Traceability Matrix

| Brief Goal | Key Requirements | Task ID(s) | Status |
|------------|-----------------|------------|--------|
| Fix Run_Sync benchmark NA results | Add IterationSetup to EnqueueBenchmarks for Run_Sync | 001 | Covered |
| Investigate EnqueueAsync Capacity=128 | Benchmark with channel options variants, document findings | 002 | Covered |
| Eliminate event stream struct boxing | Change event types from struct to class, update Channel type | 003, 004 | Covered |
| Update EventStreamAllocationBenchmarks | Validate 0 B after boxing fix | 004 | Covered |
| Update benchmark results document | Document new results | 005 | Covered |

## Task Breakdown

### Task 001: Fix Run_Sync Benchmark to Produce Valid Results

**Phase:** RED → GREEN → REFACTOR

**Analysis:**
The current `EnqueueBenchmarks.Run_Sync()` returns NA because:
- `Run()` throws `InvalidOperationException` when the channel is full
- BenchmarkDotNet runs thousands of iterations in GlobalSetup'd orchestrator
- The worker drains items, but under sustained load the channel fills → exception → BDN marks as NA
- Solution: Split `Run_Sync` and `TryRun_Sync` into a separate benchmark class with `IterationSetup` that creates a fresh orchestrator per iteration (same pattern as `DecoratorOverheadBenchmarks`)

**TDD Steps:**
1. [RED] Write benchmark test: `SyncEnqueueBenchmarks` class with `IterationSetup`
   - File: `src/Bifrost.Benchmarks/Core/SyncEnqueueBenchmarks.cs`
   - Expected failure: Compilation succeeds but need to verify Run_Sync produces numeric results (not NA)
   - Verification: Run `dotnet run -c Release --project src/Bifrost.Benchmarks -- -f '*SyncEnqueue*' --job Short` — must produce numeric Mean values

2. [GREEN] Implement `SyncEnqueueBenchmarks` with:
   - `[IterationSetup]` creating fresh `WorkOrchestrator<int>` with `Capacity = 10000, WorkerCount = 1`
   - `[IterationCleanup]` disposing via `.DisposeAsync().AsTask().GetAwaiter().GetResult()` (with `#pragma warning disable VSTHRD002`)
   - `Run_Sync()` benchmark method calling `_orchestrator!.Run(42)`
   - `TryRun_Sync()` benchmark method calling `_orchestrator!.TryRun(42)` (as comparison baseline)
   - `[MemoryDiagnoser]` attribute
   - File: `src/Bifrost.Benchmarks/Core/SyncEnqueueBenchmarks.cs`

3. [REFACTOR] Remove `Run_Sync` and `TryRun_Sync` methods from `EnqueueBenchmarks.cs`
   - These methods now live in the dedicated `SyncEnqueueBenchmarks` class
   - File: `src/Bifrost.Benchmarks/Core/EnqueueBenchmarks.cs`

**Verification:**
- [ ] Run_Sync produces numeric results (not NA)
- [ ] TryRun_Sync results are consistent with previous measurements (~30-40 ns)
- [ ] Both show 0 B allocation
- [ ] EnqueueBenchmarks still works for TryEnqueue and EnqueueAsync

**Dependencies:** None
**Parallelizable:** Yes (Group A)

---

### Task 002: Investigate and Document EnqueueAsync Capacity Behavior

**Phase:** RED → GREEN → REFACTOR

**Analysis:**
EnqueueAsync at Capacity=128 shows 181 ns mean and 1 B allocation vs Capacity=1024 showing 102 ns and 0 B. This is `Channel<T>` runtime behavior — when the bounded channel is under pressure with small buffers, `WriteAsync` completes asynchronously causing ValueTask to box. Bifrost cannot fix this, but we can:
1. Add a benchmark variant with `SingleWriter = true` to see if it helps
2. Document capacity sizing guidance

**TDD Steps:**
1. [RED] Add `SingleWriter` parameter to `EnqueueBenchmarks`
   - File: `src/Bifrost.Benchmarks/Core/EnqueueBenchmarks.cs`
   - Add `[Params(false, true)] public bool SingleWriter { get; set; }` property
   - Pass `SingleWriter` to `BoundedChannelOptions` in `GlobalSetup`
   - Expected: Compilation succeeds, need to run benchmarks to measure

2. [GREEN] Run benchmarks and capture results
   - Run: `dotnet run -c Release --project src/Bifrost.Benchmarks -- -f '*EnqueueBenchmarks*' --job Short`
   - Capture results comparing SingleWriter=false vs SingleWriter=true at both capacities

3. [REFACTOR] Based on results, either:
   - Keep `SingleWriter` param if it shows meaningful improvement, OR
   - Remove it and document that SingleWriter doesn't help (since orchestrator allows multiple producers)
   - Update the `WorkOrchestrator` XML docs with capacity sizing guidance

**Verification:**
- [ ] Benchmark runs with both SingleWriter values
- [ ] Results documented
- [ ] Capacity sizing guidance added to XML docs or benchmark results

**Dependencies:** None
**Parallelizable:** Yes (Group A)

---

### Task 003: Eliminate Event Stream Struct Boxing

**Phase:** RED → GREEN → REFACTOR

**Analysis:**
The boxing occurs because:
1. Events are `readonly record struct` implementing `IOrchestratorEvent` (an interface)
2. Subscriber channels are `Channel<IOrchestratorEvent>` (interface-typed)
3. `TryWrite(evt)` boxes the struct to satisfy the interface constraint

**Solution: Convert event types from `readonly record struct` to `sealed record class`**

This is the simplest approach because:
- Record classes are heap-allocated once at creation (not per-subscriber)
- No boxing occurs when writing to `Channel<IOrchestratorEvent>` since classes are already reference types
- With broadcast to N subscribers, struct boxing creates N heap copies vs class creating 1
- All existing tests pass without changes (verified by exploration — tests don't rely on value semantics)
- `IOrchestratorEvent` interface and `ICorrelatedEvent` interface remain unchanged
- `GetEventStreamAsync<TEvent>` generic constraint still works

**Trade-off:** Each event now allocates on creation (the struct was stack-allocated). But events were *already* being heap-allocated via boxing at the channel boundary, so the net effect is fewer allocations (1 per event vs 1 per event per subscriber).

**TDD Steps:**
1. [RED] Write tests verifying event types are reference types
   - File: `src/Bifrost.Tests/Events/OrchestratorEventTypeTests.cs`
   - Tests:
     - `WorkEnqueuedEvent_IsReferenceType_ReturnsTrue` — `Assert.That(typeof(WorkEnqueuedEvent<int>).IsValueType).IsFalse()`
     - `WorkCompletedEvent_IsReferenceType_ReturnsTrue`
     - `ScalingEvent_IsReferenceType_ReturnsTrue`
   - Expected failure: Tests fail because events are currently value types (structs)
   - Run: `dotnet test src/Bifrost.Tests` — MUST FAIL

2. [GREEN] Convert event types from struct to class
   - File: `src/Bifrost.Core/Events/WorkEnqueuedEvent.cs`
     - Change `public readonly record struct WorkEnqueuedEvent<TWork>(...) : ICorrelatedEvent;`
     - To `public sealed record WorkEnqueuedEvent<TWork>(...) : ICorrelatedEvent;`
   - File: `src/Bifrost.Core/Events/WorkCompletedEvent.cs`
     - Change `public readonly record struct WorkCompletedEvent<TWork>(...) : IOrchestratorEvent;`
     - To `public sealed record WorkCompletedEvent<TWork>(...) : IOrchestratorEvent;`
   - File: `src/Bifrost.Core/Events/ScalingEvent.cs`
     - Change `public readonly record struct ScalingEvent(...) : IOrchestratorEvent;`
     - To `public sealed record ScalingEvent(...) : IOrchestratorEvent;`
   - Run: `dotnet test src/Bifrost.Tests` — MUST PASS (all 33 event stream tests)

3. [REFACTOR] No refactoring needed — the change is minimal

**Verification:**
- [ ] All 3 event type tests pass (reference type assertion)
- [ ] All existing EventStreamOrchestratorTests pass (16 tests)
- [ ] All existing EventStreamBroadcastTests pass (7 tests)
- [ ] All existing EventStreamCorrelationTests pass (10 tests)
- [ ] All other tests in the solution pass
- [ ] Build succeeds with zero warnings

**Dependencies:** None
**Parallelizable:** Yes (Group B)

---

### Task 004: Update EventStreamAllocationBenchmarks and Validate Zero-Allocation

**Phase:** RED → GREEN → REFACTOR

**Analysis:**
After Task 003 converts events to classes, the `EventStreamAllocationBenchmarks.TryEnqueue_WithEventStream` should show different allocation characteristics. The current 298 B from boxing should be replaced by the class allocation cost. However, since record classes are reference types, `TryWrite` no longer boxes — the allocation is now just the event object creation in `EventStreamOrchestrator.TryEnqueue`.

The benchmark comments and XML docs need updating to reflect the new allocation model.

**TDD Steps:**
1. [RED] Run existing `EventStreamAllocationBenchmarks` after Task 003
   - File: `src/Bifrost.Benchmarks/Allocation/EventStreamAllocationBenchmarks.cs`
   - Run: `dotnet run -c Release --project src/Bifrost.Benchmarks -- -f '*EventStreamAllocation*' --job Short`
   - Expected: Allocation value changes from 298 B (boxing) to event object size

2. [GREEN] Update benchmark XML docs and comments to reflect new allocation model
   - File: `src/Bifrost.Benchmarks/Allocation/EventStreamAllocationBenchmarks.cs`
   - Remove references to "boxing" in comments
   - Update `<remarks>` to describe the new allocation characteristics
   - Rename benchmark to `TryEnqueue_WithEventStream` (keep same name but update docs)

3. [REFACTOR] If allocation is now lower but non-zero (expected — the event object itself allocates), document the new baseline. If zero (unlikely but possible if JIT optimizes), celebrate.

**Verification:**
- [ ] Benchmark runs successfully
- [ ] New allocation value documented
- [ ] Comments accurately describe current behavior (no stale boxing references)

**Dependencies:** Task 003
**Parallelizable:** No (depends on 003)

---

### Task 005: Update Benchmark Results Documentation

**Phase:** Documentation only

**Steps:**
1. Re-run the full benchmark suite (or relevant subsets)
   - Run_Sync benchmarks: `dotnet run -c Release --project src/Bifrost.Benchmarks -- -f '*SyncEnqueue*'`
   - EnqueueAsync with SingleWriter: `dotnet run -c Release --project src/Bifrost.Benchmarks -- -f '*EnqueueBenchmarks*'`
   - Event stream allocation: `dotnet run -c Release --project src/Bifrost.Benchmarks -- -f '*EventStreamAllocation*'`

2. Update `docs/benchmarks/2026-02-06-full-suite-results.md`:
   - Add Run_Sync results to Core section
   - Update EnqueueAsync analysis with capacity sizing guidance
   - Update Event Stream allocation results
   - Update "Recommendations > Potential Future Improvements" section (mark addressed items)
   - Update Design Goal Validation table if any metrics changed

3. Update `docs/benchmarks/BENCHMARKS.md` if needed

**Verification:**
- [ ] All benchmark results are current
- [ ] Future Improvements section reflects addressed items
- [ ] No stale data in results document

**Dependencies:** Tasks 001, 002, 003, 004
**Parallelizable:** No (final task)

---

## Parallelization Strategy

```
Group A (parallel):          Group B (parallel with A):
  Task 001 (Run_Sync fix)     Task 003 (Event boxing fix)
  Task 002 (EnqueueAsync)           |
                                    v
                               Task 004 (Benchmark validation)

         \                    /
          \                  /
           v                v
           Task 005 (Documentation)
```

### Worktree Assignments

| Group | Branch | Tasks |
|-------|--------|-------|
| A | `refactor/benchmark-sync-and-capacity` | 001, 002 |
| B | `refactor/event-boxing-elimination` | 003, 004 |
| Final | Integration branch | 005 |

## Deferred Items

None — all three improvements from the benchmark results are addressed.

## Completion Checklist
- [ ] All tests written before implementation
- [ ] All tests pass
- [ ] Run_Sync benchmark produces numeric results
- [ ] Event stream boxing eliminated
- [ ] Benchmark results document updated
- [ ] Ready for review
