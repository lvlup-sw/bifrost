# Hardware×Capacity-Aware Priority-Binding Selection

**Date:** 2026-06-15
**Feature:** `cpq-binding-auto-select`
**Status:** Design
**Follows:** issue #22 (600 s DR-8 soak), `docs/benchmarks/2026-06-cpq-soak.md`

## Context

The 600 s DR-8 soak (committed `94703a0`) showed the relaxed MultiQueue binding loses
priority-ordering fidelity in the orchestrator's typical regime — few workers, a shallow queue —
on many-core hardware. The MultiQueue scatters across `n = RoundUpToPowerOf2(4 × ProcessorCount)`
sub-queues; its expected dequeue rank error is `(5/6)·n`. When `n` approaches the queue's
population, the two-choice dequeue can no longer honor the virtual-time order: at 8 workers the
MultiQueue's interactive p95 was 15.4 s against the exact lock's 1.0 s, and at 2 workers it
**exceeded the DR-5 starvation bound** (225 s vs a 99 s ceiling) where the lock held it. Throughput,
occupancy, and allocation were at parity — the relaxation buys nothing in a low-contention regime
because seconds-long work items dwarf the queue-op cost.

Today the binding is chosen by a blind static default: `UsePriorityDispatch(useLockingBinding =
false)` selects the MultiQueue regardless of hardware or capacity. Nothing routes on
`ProcessorCount` or `Capacity`, even though the orchestrator knows both at construction. This design
adds a construction-time, hardware×capacity-aware selector so the package picks the binding that can
actually honor priority order on the host it runs on, while leaving an explicit override for callers
who know their workload.

## Decision: Auto binding selection (enum, default Auto)

Selected approach (ideate **B**): replace the `useLockingBinding` bool with a tri-state intent and
resolve `Auto` at construction from the rank-error-vs-capacity relationship. This is a free change
now — priority dispatch ships first in 0.5.0, so there are no released callers; after the tag it
would be a breaking behavior change, so it lands before 0.5.0.

### DR-1: `PriorityBinding` enum, `Auto` the default

Add `public enum PriorityBinding { Auto = 0, Locking, MultiQueue }` to `Bifrost.Core`, and a
`PriorityBinding Binding { get; set; } = PriorityBinding.Auto;` property on
`PriorityDispatchOptions`. `UsePriorityDispatch` drops the `useLockingBinding` bool and exposes the
enum instead:

```csharp
builder.UsePriorityDispatch(o => o.InteractiveBoostWindow = …, binding: PriorityBinding.Auto);
```

`Auto` is the default so the common path is correct without the caller reasoning about rank error.
`Locking` and `MultiQueue` are explicit, unconditional overrides. The concrete `DispatchStrategy`
enum (`Fifo`/`PriorityLocking`/`PriorityMultiQueue`) stays the low-level factory selector in
`WorkOrchestrator`; `PriorityBinding` is the user-facing *intent* that resolves into a concrete
`DispatchStrategy`. Keeping the two layers separate preserves the DR-4 enum/factory construction
switch unchanged for the explicit cases and confines `Auto` resolution to one place.

### DR-2: The Auto heuristic

A pure, deterministic resolution: `Auto` picks `Locking` when the MultiQueue's expected rank error
reaches half the capacity (ordering materially degraded even at full occupancy), otherwise
`MultiQueue`:

```
n        = RoundUpToPowerOf2(4 × ProcessorCount)      // mirrors ConcurrentPriorityQueue's n exactly
rankErr  = (5 × n) / 6
binding  = rankErr >= Capacity / 2 ? Locking : MultiQueue
```

The `1/2` is a single named constant (`AutoLockRankErrorFraction`), tunable. Flip points it
produces (sanity): capacity 128 → MultiQueue at ≤16 cores, Locking at ≥17 (the soak's 32-thread host
→ Locking, correct); capacity 1024 → MultiQueue until ~128 cores (deep queues keep the MultiQueue,
where it both orders fine and scales); capacity 32 → Locking from ~5 cores up. The heuristic guards
the one thing knowable at construction — whether the relaxed dequeue *can* honor order. It cannot see
work-item duration (the MultiQueue's throughput win needs micro items), so when it picks `MultiQueue`
the two bindings are at worst at parity for real work and the MultiQueue wins only if the workload is
micro-item/high-contention. The choice is therefore never worse than the lock on ordering when
ordering is preserved, and never worse on throughput. `n` MUST be derived identically to
`ConcurrentPriorityQueue.s_defaultSubQueueCount`; a test asserts the resolver and the actual queue
agree (an internal accessor exposes the queue's sub-queue count).

### DR-3: Resolution location and AOT-safety

A static `PriorityBindingResolver.Resolve(PriorityBinding requested, int processorCount, int
capacity) → DispatchStrategy`, mirroring the existing `CpqTuningResolver` shape. `ProcessorCount` is
a parameter, not read from `Environment` inside the resolver, so the heuristic is unit-testable
across hardware shapes without mocking the environment. `WorkOrchestrator` calls it once in its
constructor — where `Environment.ProcessorCount` and `opts.Capacity` are both in hand — and feeds the
resulting concrete `DispatchStrategy` into the existing factory switch. The path stays
reflection-free and codegen-free (DR-9 AOT/trim posture): an enum switch over sealed constructors, no
new dynamic behavior. `RoundUpToPowerOf2` is `System.Numerics.BitOperations`, already used by the
queue and AOT-safe.

### DR-4: Explicit override and observability

Explicit `Locking`/`MultiQueue` always win — the resolver returns them unconditionally without
consulting the heuristic. `Auto` resolves exactly once, at construction. Because `Auto` makes the
effective binding implicit, the resolved choice is surfaced two ways: (1) logged once at
construction via the existing `ILogger<WorkOrchestrator<TWork>>` at `Information` (binding, the
`Auto`/explicit origin, `ProcessorCount`, `Capacity`, and the computed rank error), and (2) exposed
as a read-only `ResolvedBinding` so a test or a consumer can assert what `Auto` chose without parsing
logs. No runtime re-resolution or binding swap — the decision is fixed for the orchestrator's
lifetime.

### DR-5: The starvation bound is binding-dependent (doc #2)

The DR-5 by-construction starvation bound is a property of the *exact-ordering* locking binding: an
item older than the boost window outranks every fresh arrival, and the exact min-key dequeue then
must pick it. The relaxed MultiQueue gives no such guarantee — its dequeue picks the min of two
sampled sub-queues, so an old item can be passed over (soak: exceeded at 2 workers). The README's
generic "the starvation bound holds by construction" (≈ line 188) is corrected to scope the
guarantee to the locking binding and note the MultiQueue is best-effort. With `Auto` the default
biasing toward the lock on the orchestrator's typical regime, the hard bound is what most consumers
get; those who need it unconditionally force `PriorityBinding.Locking`.

### DR-6: Documentation corrections

- **#2 — README** (`README.md` ≈ line 188): scope the starvation-bound claim to the locking binding
  per DR-5; update the "Choosing a strategy" section for the new `Auto` default and the
  `PriorityBinding` API.
- **#3 — soak doc** (`docs/benchmarks/2026-06-cpq-soak.md`): one line in the Environment/Harness
  noting the MultiQueue ran the `Balanced` profile, which for the value-type `WorkEnvelope<int>`
  resolves to `(stickiness 1, buffering 0)` — its tightest ordering — so the comparison is not a
  tuning artifact.

## Acceptance criteria

- `PriorityBinding { Auto, Locking, MultiQueue }` exists in `Bifrost.Core`; `PriorityDispatchOptions
  .Binding` defaults to `Auto`; `UsePriorityDispatch` exposes the enum and no longer takes
  `useLockingBinding`.
- `PriorityBindingResolver.Resolve` is pure and unit-tested: explicit values pass through; `Auto`
  returns `Locking` when `(5·n)/6 ≥ Capacity/2` and `MultiQueue` otherwise, across a matrix of
  `ProcessorCount` × `Capacity` covering both sides of every flip point.
- A test asserts the resolver's `n` equals `ConcurrentPriorityQueue`'s actual sub-queue count.
- `WorkOrchestrator` resolves `Auto` once at construction, exposes `ResolvedBinding`, and logs the
  decision; explicit overrides bypass the heuristic.
- AOT smoke + the full TUnit suite green; no new trim/AOT warnings.
- README and soak-doc corrections (DR-6) applied.

## Out of scope

- Making the MultiQueue's sub-queue count `n` capacity-aware (a `Bifrost.Concurrency` change; the
  binding-level selector subsumes the need here).
- Runtime binding swap or adaptive re-resolution (construction-time only).
- Detecting work-item duration to predict the MultiQueue's throughput upside (not knowable at
  construction).
