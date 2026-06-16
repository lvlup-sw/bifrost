# Implementation Plan — Hardware×Capacity-Aware Priority-Binding Selection

**Design:** `docs/designs/2026-06-15-cpq-binding-auto-select.md`
**Date:** 2026-06-15
**Feature:** `cpq-binding-auto-select`

Iron law: no production code without a failing test first. TUnit, assertions awaited. .NET 10,
AOT-safe (reflection-free, enum/factory). Bump package versions only in
`src/Directory.Packages.props` (none expected here).

## Architecture summary

- `PriorityBinding { Auto = 0, Locking, MultiQueue }` (new, `Bifrost.Core`) — user-facing intent.
- `PriorityDispatchOptions.Binding` (new, default `Auto`) — carries the intent.
- `DispatchStrategy.Priority` (new sentinel) — "priority dispatch; resolve concrete binding from
  `PriorityDispatchOptions.Binding` at construction." Existing `PriorityMultiQueue`/`PriorityLocking`
  stay as direct concrete selectors.
- `PriorityBindingResolver.Resolve(PriorityBinding, int processorCount, int capacity) →
  DispatchStrategy` (new, `src/Bifrost/Queues/`, mirrors `CpqTuningResolver`) — pure, deterministic:
  explicit pass-through; `Auto` → `PriorityLocking` when `(5·n)/6 ≥ Capacity/2` else
  `PriorityMultiQueue`, with `n = RoundUpToPowerOf2(4 × processorCount)`.
- `UsePriorityDispatch(configure, PriorityBinding binding = Auto)` — drops the `useLockingBinding`
  bool; sets `DispatchStrategy.Priority` + `Priority.Binding`.
- `WorkOrchestrator` ctor — resolves `Priority` sentinel via the resolver
  (`Environment.ProcessorCount`, `opts.Capacity`), feeds the concrete result into the existing
  factory switch, exposes `PriorityBinding? ResolvedBinding`, logs the decision.

---

## Group A — Bifrost.Core types (parallel-safe; distinct files)

### Task 1: `PriorityBinding` enum
**Phase:** RED → GREEN

1. [RED] `PriorityBinding_DefaultValue_IsAuto` and `PriorityBinding_DeclaresLockingAndMultiQueue`
   - File: `src/Bifrost.Tests/Core/PriorityBindingTests.cs`
   - Expected failure: type does not exist.
2. [GREEN] Add `public enum PriorityBinding { Auto = 0, Locking, MultiQueue }` with XML docs.
   - File: `src/Bifrost.Core/PriorityBinding.cs`

**Dependencies:** None
**Parallelizable:** Yes

### Task 2: `DispatchStrategy.Priority` sentinel
**Phase:** RED → GREEN

1. [RED] `DispatchStrategy_DefaultValue_IsFifo` and `DispatchStrategy_DeclaresPrioritySentinel`
   - File: `src/Bifrost.Tests/Core/DispatchStrategyTests.cs`
   - Expected failure: `Priority` member absent.
2. [GREEN] Add `Priority` member to `DispatchStrategy` with XML doc explaining deferred resolution;
   keep `Fifo = 0`, `PriorityMultiQueue`, `PriorityLocking`.
   - File: `src/Bifrost.Core/DispatchStrategy.cs`

**Dependencies:** None
**Parallelizable:** Yes

### Task 3: `PriorityDispatchOptions.Binding` property
**Phase:** RED → GREEN

1. [RED] `PriorityDispatchOptions_Binding_DefaultsToAuto`
   - File: `src/Bifrost.Tests/Core/PriorityDispatchOptionsBindingTests.cs`
   - Expected failure: property absent.
2. [GREEN] Add `public PriorityBinding Binding { get; set; } = PriorityBinding.Auto;` with XML doc.
   - File: `src/Bifrost.Core/PriorityDispatchOptions.cs`

**Dependencies:** Task 1
**Parallelizable:** No (after Task 1)

---

## Group B — Resolver + n-agreement (sequential; shared resolver file)

### Task 4: Resolver explicit pass-through
**Phase:** RED → GREEN

1. [RED] `Resolve_ExplicitLocking_ReturnsPriorityLocking`,
   `Resolve_ExplicitMultiQueue_ReturnsPriorityMultiQueue` (any processorCount/capacity)
   - File: `src/Bifrost.Tests/Queues/PriorityBindingResolverTests.cs`
   - Expected failure: resolver does not exist.
2. [GREEN] Add `internal static class PriorityBindingResolver` with `Resolve(...)` handling the two
   explicit cases.
   - File: `src/Bifrost/Queues/PriorityBindingResolver.cs`

**Dependencies:** Task 1, Task 2
**Parallelizable:** No

### Task 5: Resolver `Auto` heuristic
**Phase:** RED → GREEN → REFACTOR

1. [RED] `Resolve_Auto_PicksLockingWhenRankErrorReachesHalfCapacity` and
   `Resolve_Auto_PicksMultiQueueBelowThreshold` — matrix straddling flip points:
   (cap 128, PC 16 → MultiQueue), (cap 128, PC 17/32 → Locking), (cap 1024, PC 32 → MultiQueue),
   (cap 32, PC 8 → Locking).
   - File: `src/Bifrost.Tests/Queues/PriorityBindingResolverTests.cs`
   - Expected failure: `Auto` arm not implemented.
2. [GREEN] Implement: `n = (int)BitOperations.RoundUpToPowerOf2((uint)(4 * processorCount))`,
   `rankErr = (5 * n) / 6`, `rankErr >= capacity * AutoLockRankErrorFraction ? PriorityLocking :
   PriorityMultiQueue`.
   - File: `src/Bifrost/Queues/PriorityBindingResolver.cs`
3. [REFACTOR] Extract `AutoLockRankErrorFraction` (=0.5) and an `internal static int
   SubQueueCountFor(int processorCount)` reused by Task 6; XML-document the heuristic + tunable.

**Dependencies:** Task 4
**Parallelizable:** No

### Task 6: Resolver `n` matches the queue's actual sub-queue count
**Phase:** RED → GREEN

1. [RED] `SubQueueCountFor_MatchesConcurrentPriorityQueueActualCount` — compares
   `PriorityBindingResolver.SubQueueCountFor(Environment.ProcessorCount)` to a freshly-built
   `ConcurrentPriorityQueue`'s actual sub-queue count.
   - File: `src/Bifrost.Tests/Queues/PriorityBindingResolverTests.cs`
   - Expected failure: no accessor exposing the queue's sub-queue count.
2. [GREEN] Add `internal int SubQueueCount` (or equivalent) to `ConcurrentPriorityQueue`; ensure
   `InternalsVisibleTo("Bifrost.Tests")` and (if needed) `"Bifrost"` on `Bifrost.Concurrency`.
   - Files: `src/Bifrost.Concurrency/ConcurrentPriorityQueue.cs` (+ AssemblyInfo/csproj IVT if absent)

**Dependencies:** Task 5
**Parallelizable:** No

---

## Group C — Wiring (after A+B; distinct files → parallel within)

### Task 7: `UsePriorityDispatch` enum API
**Phase:** RED → GREEN

1. [RED] `UsePriorityDispatch_DefaultBinding_SetsPrioritySentinelAndAutoIntent`,
   `UsePriorityDispatch_ExplicitLocking_SetsBindingLocking` (assert
   `options.DispatchStrategy == Priority` and `options.Priority.Binding == given`)
   - File: `src/Bifrost.Tests/DependencyInjection/PriorityDispatchExtensionsTests.cs`
   - Expected failure: signature still has `useLockingBinding` bool.
2. [GREEN] Replace `bool useLockingBinding = false` with `PriorityBinding binding =
   PriorityBinding.Auto`; set `options.DispatchStrategy = DispatchStrategy.Priority` and
   `options.Priority.Binding = binding`; refresh XML doc (drop the stale "Indicative soak
   measurements" line; point at the release soak + `Auto`).
   - File: `src/Bifrost/DependencyInjection/PriorityDispatchExtensions.cs`

**Dependencies:** Task 2, Task 3
**Parallelizable:** Yes (with Task 8)

### Task 8: `WorkOrchestrator` resolves at construction + `ResolvedBinding` + log
**Phase:** RED → GREEN → REFACTOR

1. [RED] `Constructor_AutoOnManyCoreShallowQueue_ResolvesLocking` (force a high effective
   ProcessorCount/low capacity path → `ResolvedBinding == Locking`),
   `Constructor_ExplicitMultiQueue_BypassesHeuristic`,
   `Constructor_Fifo_ResolvedBindingIsNull`.
   - File: `src/Bifrost.Tests/WorkOrchestratorBindingResolutionTests.cs`
   - Expected failure: no `ResolvedBinding`; sentinel unhandled.
2. [GREEN] Before the factory switch, map `DispatchStrategy.Priority` →
   `PriorityBindingResolver.Resolve(opts.Priority.Binding, Environment.ProcessorCount,
   opts.Capacity)`; switch on the concrete result; set `ResolvedBinding`
   (`PriorityLocking→Locking`, `PriorityMultiQueue→MultiQueue`, `Fifo→null`); add the
   `Priority` case-arm dispatch. Log the decision once via `_logger` at `Information`.
   - File: `src/Bifrost/WorkOrchestrator.cs`
3. [REFACTOR] Expose `public PriorityBinding? ResolvedBinding { get; }`; keep the switch
   exhaustive (existing `_ => throw`).

   Note: if `ProcessorCount` cannot be injected for the test, gate the high-core path by asserting
   the resolver decision directly and the orchestrator's pass-through of an explicit binding; the
   resolver matrix (Task 5) already proves the heuristic across hardware shapes.

**Dependencies:** Task 5, Task 3, Task 6
**Parallelizable:** Yes (with Task 7)

---

## Group D — Documentation (parallel-safe; no tests)

### Task 9: README corrections (#2)
**Phase:** N/A (docs)

1. Scope the starvation-bound claim (README ≈ line 188) to the exact-ordering locking binding; note
   the relaxed MultiQueue is best-effort (soak exceeded it at 2 workers).
2. Update "Choosing a strategy" for the `PriorityBinding` API and the `Auto` default.
   - File: `README.md`

**Dependencies:** None (content), but final wording should match the shipped API (coordinate with
Task 7). Apply last.
**Parallelizable:** Yes

### Task 10: Soak-doc note (#3)
**Phase:** N/A (docs)

1. Add one line to the Environment/Harness: the MultiQueue ran the `Balanced` profile, which for the
   value-type `WorkEnvelope<int>` resolves to `(stickiness 1, buffering 0)` — its tightest ordering —
   so the comparison is not a tuning artifact.
   - File: `docs/benchmarks/2026-06-cpq-soak.md`

**Dependencies:** None
**Parallelizable:** Yes

### Task 11: CHANGELOG `[Unreleased]` update (0.5.0 release notes)
**Phase:** N/A (docs)

1. Refresh the priority-dispatch entry (CHANGELOG.md ≈ lines 68–69) for the shipped API: the
   `UsePriorityDispatch()` binding is now selected via `PriorityBinding { Auto, Locking, MultiQueue }`
   (default `Auto`, hardware×capacity-aware) rather than a `useLockingBinding` bool; note the new
   `DispatchStrategy.Priority` resolution sentinel and that the by-construction starvation bound is
   the locking binding's guarantee (the relaxed MultiQueue is best-effort). Keep it in `[Unreleased]`.
   - File: `CHANGELOG.md`

**Dependencies:** Task 7 (wording must match the shipped API). Apply last with Task 9.
**Parallelizable:** Yes (content), apply after Task 7

---

## Execution order / parallelization

1. **Wave 1 (parallel):** Task 1, Task 2, Task 10 (docs).
2. **Wave 2:** Task 3 (after 1).
3. **Wave 3 (sequential chain):** Task 4 → Task 5 → Task 6.
4. **Wave 4 (parallel):** Task 7, Task 8 (after waves 2–3).
5. **Wave 5 (parallel):** Task 9 (README) + Task 11 (CHANGELOG), after Task 7 settles the API.

Final gate: full TUnit suite (`Bifrost.Tests`) green in Release; AOT smoke green; no new trim/AOT
warnings; `ResolvedBinding` logged once.
