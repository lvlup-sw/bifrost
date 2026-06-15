# Design: Buffered MultiQueue — per-sub-queue insertion + deletion buffers (ESA 2021 §4)

**Date:** 2026-06-15
**Issue:** [#26](https://github.com/lvlup-sw/bifrost/issues/26) · **Milestone:** #2 (CPQ — ESA 2021 parity)
**Design input (invariants):** `docs/research/2026-06-15-cpq-buffering-spike.md` (verdict **GO**, directional)
**Workflow:** `cpq-buffered-multiqueue` (feature)

## Problem Statement

The discovery spike established (directionally, three converging lines of evidence) that the dense
`ConcurrentPriorityQueue` path is **locality-bound at the per-op floor**: a delete walks an
arity-4 heap across `log₄(N)` mostly-cold cache lines, and single-thread latency rises 80 → 176 ns
with heap depth precisely because of that walk. ESA 2021 §4 buffering removes the deep-heap touch
from the hot path — a sorted **deletion buffer** `D` serves pops, an **insertion buffer** `I`
absorbs pushes, and the heap is touched only on `I`-flush or `D`-refill (~once per `C` ops). This
design realizes that in idiomatic .NET while treating the spike's findings as fixed invariants.

**Fixed invariants (do not move):** GO; `C = 16` (paper optimum); **keep arity-4** (a deliberate
.NET write-barrier choice, not the paper's k=8); preserve the seqlock-published top, the
`TryDequeueVerificationScan` false-authority, and occupancy-bitmask routing with **zero contract
change**; `0 B/op`; AOT/trim-safe; quality is **not** assumed neutral (A/B-gated); formal
`perf`-stat cache-miss confirmation deferred to **#30**.

## Phase 2 — Approaches considered

### Option A: `[InlineArray]` buffers, integrated into `SubQueue`

**Approach:** Two `[InlineArray(16)]` generic buffer structs embedded as fields on the existing
`SubQueue`; storage lives inline in the sub-queue object. Moves use `Span.CopyTo`/`Span.Clear`
gated on `RuntimeHelpers.IsReferenceOrContainsReferences<T>()`.

**Pros:**
- Best locality — buffer storage is inline with the object, no extra heap object, no pointer-chase.
- Idiomatic modern .NET (C# 12 inline arrays are the safe-code replacement for fixed buffers); the
  faithful equivalent of the C++ reference's inline `std::array<value_type,16>`.
- Trivially `0 B/op`; AOT-safe (pure layout/codegen, no reflection).

**Cons:**
- `InlineArray(N)` fixes storage size at **compile time** — the runtime knob caps the *logical* size
  within the fixed-16 storage, it cannot grow beyond 16 without a rebuild. (Non-loss: 16 is the
  paper's single optimum and the C++ reference also compile-time-fixes it.)

**Best when:** the optimization target *is* cache locality and the optimum buffer size is known —
exactly this case.

### Option B: Plain once-allocated `(TElement,TPriority)[]` buffers, integrated

**Approach:** Two arrays allocated once per sub-queue at construction, runtime-sized.

**Pros:** simplest; arbitrarily runtime-tunable; `0 B/op` steady-state.
**Cons:** adds a heap object **and a pointer-chase indirection** on the very hot path we are making
cache-tight — the `std::vector` realization, leaving locality (the goal) on the table.
**Best when:** capacity must vary widely at runtime — not true here (16 is the optimum).

### Option C: Separate `BufferedSubQueue` type (strategy)

**Approach:** A parallel buffered type; the proven `SubQueue` stays untouched.
**Pros:** maximum isolation; cleanest two-type A/B.
**Cons:** most code — duplicates/abstracts ~39 KB of heap+seqlock+occupancy logic; two paths to keep
from diverging; hot-path dispatch concerns.
**Best when:** the buffered and unbuffered paths must coexist permanently as distinct types.

## Chosen Approach

**Option A**, with an **opt-in `bufferCapacity` ctor knob defaulting to 0 (off)** until #30 confirms.
Rationale: A is the idiomatic-.NET realization the official guidance points to and the one that best
serves the locality goal; its only trade-off (compile-time-fixed storage) is a non-loss. Default-off
mirrors `stickiness: 1` ("the public contract matches the pre-stickiness behavior exactly") and
.NET's don't-change-default-behavior principle, so no existing consumer is touched before the formal
Xeon A/B + rank-error confirmation lands. The knob's `[0,16]` range doubles as the A/B sweep dial
(test C = 4/8/16 without recompiling). Enabling is a single predictable branch in push/pop.

```
            push(v)                                   pop()  ──► returns D.front() (= min)
              │                                          │
   ┌──────────┴───────────┐                              ▼
   │ v ≤ max(D) & D used?  │── yes ─► sorted-insert ► D   D.front() removed;  if D now empty:
   └──────────┬───────────┘   (evict max(D)→I if full)        refill:  flush I→heap,
              │ no                                              then pop min(16,|heap|) → D (sorted)
        append to I ;  if I full ► flush I→heap, heap-push v
                              ▲                                ▲
   ARITY-4 HEAP  ────────────┴──────── touched ONLY on ───────┘  (≈ once per C ops)
   Invariant:  D empty  ⟺  I empty ∧ heap empty  ⟺  whole sub-queue empty
   Published seqlock top = D.front();  EmptyFlag = (D empty);  occupancy bit set ⟺ D non-empty
```

## Requirements

### DR-1: Inline buffer storage via `[InlineArray]`

Two compile-time-fixed inline buffers embedded in `SubQueue`: an **unsorted insertion buffer** and a
**sorted deletion buffer**, each backed by a generic `[InlineArray(BufferCapacityMax)] struct
SubQueueBuffer<TElement,TPriority> { private (TElement,TPriority) _e0; }` with
`BufferCapacityMax = 16`. Element access via the language indexer / `MemoryMarshal.CreateSpan`.

**Acceptance criteria:**
- The buffer struct has exactly one instance field and the `InlineArray(16)` attribute; compiles
  with no CS9167/9168/9169/9180/9184.
- For `(int,int)` and `(string,string)` and `(object,long)` instantiations, the type builds and a
  NativeAOT publish of the AOT-smoke sample is warning-clean (no IL2xxx/IL3050).
- No per-operation heap allocation introduced (`[MemoryDiagnoser]` shows `0 B/op` on the buffered
  Enqueue+Dequeue pair).

### DR-2: Buffered push/pop algorithm + the emptiness invariant

Implement the reference `BufferedPQ` semantics over the arity-4 heap: push routes to `I` (or
sorted-inserts into `D` when `v ≤ max(D)`, or direct-to-`D` when the structure is otherwise empty);
pop returns and removes `D.front()` and refills `D` when it empties; `I` flushes to the heap when
full; refill flushes `I` then pops the smallest `min(16,|heap|)` into `D` sorted. Maintain the
invariant **`D` empty ⟺ `I` empty ∧ heap empty ⟺ sub-queue empty**.

**Acceptance criteria:**
- Given any interleaving of pushes/pops on one buffered sub-queue, When the sequence runs, Then the
  per-sub-queue dequeue order is **identical** to the unbuffered arity-4 heap order (differential
  test vs. the existing `SubQueue`).
- Given `D` empties on a pop with the heap non-empty, When refill runs, Then `D` holds the smallest
  `min(16,|heap|)` elements in sorted order and `D.front()` is the sub-queue minimum.
- Given a delete observes `D` empty, Then `I` and the heap are also empty (invariant; debug-asserted).

### DR-3: Zero-contract-change to the published top, false-authority, and occupancy

The seqlock-published top becomes `D.front()`; `EmptyFlag` becomes `(D empty)`; both are republished
under the existing lock on every pop, every front-changing push, and every refill. `TryReadTop` and
the lock-free two-choice sampling path are **unchanged** and never touch the buffers. The occupancy
bit is set/cleared on the `D` 0↔non-0 boundary (replacing the heap 0↔1 boundary).
`TryDequeueVerificationScan` remains the sole authority allowed to return `false`.

**Acceptance criteria:**
- The lock-free reader (`TryReadTop`, Dequeue sampling) reads only the published top slot — verified
  by inspection/test that no sampling path dereferences a buffer field.
- Given a buffered sub-queue with `D` non-empty, Then its occupancy bit is set; given `D` drains to
  empty, Then the bit clears (boundary-only `Interlocked.Or/And`, as today).
- The full `Bifrost.Tests.Concurrency` suite passes in **Release** with buffering enabled, including
  the existing honest-emptiness, conservation, and false-scan tests (no contract regression).

### DR-4: Write-barrier-safe buffer moves (.NET realization risk)

All buffer moves — sorted-insert shift, `max(D)`→`I` eviction, `I`→heap flush, heap→`D` refill, and
slot clears — go through `Span.CopyTo` / `Span.Clear` over the inline buffers, with **reference
clears gated** on `RuntimeHelpers.IsReferenceOrContainsReferences<(TElement,TPriority)>()`. No
`MemoryMarshal.AsRef`/`Unsafe` reinterpret or SIMD path is applied to reference-containing tuples
(official guidance forbids bypassing the write barrier on GC refs; `AsRef<T>` throws for managed
`T`).

**Acceptance criteria:**
- For a value-type-only instantiation (`(int,int)`), buffer moves compile to barrier-free block
  copies (the runtime's `memmove` path) — **zero GC write barriers** on the buffered hot path
  (verified via the value-type Enqueue+Dequeue benchmark staying within noise of unbuffered and
  `0 B/op`).
- For a reference instantiation (`(object,long)`), moves use barrier-correct `Span.CopyTo`; no
  `ArgumentException` from `AsRef`-style APIs; no stale references retained after a flush/refill
  (gated `Span.Clear` of vacated slots).

### DR-5: Opt-in `bufferCapacity` knob (default OFF); keep arity-4

Add `bufferCapacity` to the `ConcurrentPriorityQueue` constructor chain (alongside `boundedCapacity`,
`stickiness`), range `[0, BufferCapacityMax]`, **default 0 = unbuffered**. `0` ⇒ push/pop bypass the
buffers and operate directly on the heap, **bit-exact** with current behavior. `1..16` ⇒ buffered
with that logical cap within the fixed-16 inline storage. The heap remains **arity-4**; buffering
wraps it, never replaces it.

**Acceptance criteria:**
- Given the default/parameterless constructor, Then buffering is disabled and Enqueue/Dequeue
  ordering, `Count`, rank error, and allocation are **identical** to the pre-feature queue
  (regression-locked by the existing benchmark/quality gates).
- Given `bufferCapacity` outside `[0,16]`, When constructing, Then `ArgumentOutOfRangeException` is
  thrown (matching `ValidateBoundedCapacity`), with a message naming the compile-time max.
- Given `bufferCapacity > 0`, Then the buffered path is active with logical cap = `bufferCapacity`.

### DR-6: Error handling, edge cases, and the `Count` integration

**Count must include buffered elements.** Per-sub-queue logical count becomes
`insertionCount + deletionCount + heapSize` (the reference's
`size() = insertion_end_ + deletion_end_ + pq_.size()`); the striped snapshot `Count` and the
watermark-admission view must observe buffered elements or admission will over-fill.

**Acceptance criteria:**
- Given elements resident across `I`, `D`, and the heap, When `Count` is read, Then it equals the
  total resident element count (not just heap size); a conservation test over a buffered run holds
  exactly.
- Given `D` is full and `I` is full and a `v ≤ max(D)` arrives, When push runs, Then the eviction
  cascade (`max(D)`→flush `I`→heap-push, then sorted-insert `v`) preserves all elements and order.
- Given refill with `|heap| < 16`, Then `D` receives exactly `|heap|` elements and the heap empties.
- Given bounded capacity is set with buffering on, Then watermark admission/`EnqueueResult` shedding
  behaves identically to the unbuffered bounded queue at the same total population.
- Given a pop finds `D` empty while `I`/heap are non-empty (invariant violation), Then a debug
  assertion fires (guards an implementation bug, never reachable in correct operation).

### DR-7: `BIFROST_TEST_HOOKS` instrumentation

Add gated counters mirroring the transition-bitmask pattern: buffer flushes, refills, buffered-pop
hits (served from `D` without touching the heap), direct-to-`D` inserts, eviction cascades. Fields +
`…ForTest` getters stay **ungated** (so the stripped publish build still compiles); increment
**sites** are gated behind `#if BIFROST_TEST_HOOKS`.

**Acceptance criteria:**
- The counters exist with `…ForTest` getters; a publish build with `-p:BifrostTestHooks=false`
  compiles and strips the increments.
- A test asserts the locality mechanism: over a steady buffered run, heap refills occur
  ≈ once per `bufferCapacity` pops (the amortization claim).

### DR-8: A/B gate (merge) with formal run deferred to #30

The merge gate toggles `bufferCapacity` 0 vs. 16 and requires: dense throughput up at scale; the
single-thread latency-vs-depth slope flattens; `0 B/op` preserved; rank-error gates green
(mean ≤ 2·(5/6)·n·s, P99 ≤ 10·n·s); full Concurrency suite green in Release. The formal `perf`-stat
cache-misses/op A/B on the Xeon (n=256/64T) is **#30**.

**Acceptance criteria:**
- A buffered-vs-unbuffered A/B is captured under `docs/benchmarks/`, with the GO/NO-GO decision
  recorded: **NO-GO** (and #26 closed negative) if throughput is flat at scale **or** rank error
  regresses past gate; **GO** otherwise (and the default-on flip tracked against #30).

## Technical Design

**Storage.** `SubQueue<TElement,TPriority>` gains three fields: `SubQueueBuffer<...> _insertion`,
`SubQueueBuffer<...> _deletion` (each `[InlineArray(16)]`), and `int _insertionCount`,
`int _deletionCount`, plus the readonly `_bufferCapacity` (logical cap; 0 = off). The inline structs
are accessed under the existing `SyncLock` via spans:
`MemoryMarshal.CreateSpan(ref Unsafe.As<TBuffer,(TElement,TPriority)>(ref buf), _bufferCapacity)`.
Because buffers are mutated only by the lock holder and read only by the lock holder, they add **no
new cross-thread sharing** — the lock-free published top stays the only lock-free reader surface, so
the 128-byte padding / false-sharing story is unchanged.

**Control flow.** `TryLockedPush`/`PopHeldRoot` branch on `_bufferCapacity > 0`. The buffered branch
implements the reference rules (push→I / sorted-D / direct-D; pop→D + refill; flush; refill); the
unbuffered branch is today's code verbatim. `PublishTop` is called with `D.front()` (or empty) after
each mutation — same call sites, new argument source. `Count` write becomes
`_insertionCount + _deletionCount + _size`.

**Moves.** A small set of helpers over `Span<(TElement,TPriority)>`: `ShiftInsert`, `EvictMax`,
`FlushInsertion`, `RefillDeletion`, each using `CopyTo` and gated `Clear`. The value-type fast path
falls out automatically from the runtime's `CopyTo` specialization.

## Integration Points

- `SubQueue.cs` — buffer fields, buffered push/pop, refill/flush, `PublishTop`/`Count`/occupancy at
  the `D`-boundary (replacing the heap boundary), gated counters.
- `ConcurrentPriorityQueue.cs` — `_bufferCapacity` field; new ctor parameter + `ValidateBufferCapacity`;
  thread the value to each `SubQueue`. `ConcurrentPriorityQueue.Count.cs` already sums `_header.Count`
  — no change once the per-sub-queue Count includes buffers.
- `ConcurrentPriorityQueue.Dequeue.cs` — **no change** to sampling, routing, or the verification scan
  (they consume the published top / occupancy, both contract-stable).
- `Bifrost.Benchmarks/Concurrency` — A/B verb toggling `bufferCapacity`; reuse the throughput sweep
  and `CpqSingleThreadedLatencyBenchmarks` population axis.

## Testing Strategy

Differential tests (buffered vs. unbuffered, identical seeds → identical per-sub-queue order); the
existing conservation / honest-emptiness / false-scan suites run with buffering on; a `Count`
conservation test across `I`/`D`/heap; bounded-capacity admission parity; AOT-smoke publish for the
`(object,long)` path; `[MemoryDiagnoser]` `0 B/op` on both value-type and reference instantiations;
the DR-7 amortization assertion; the DR-8 A/B harness. TDD red-first per task. Verify in **Release**
(per the project's async-timing lesson, Debug-green ≠ Release-green).

## Open Questions

- **Compile-time max = 16?** Fixed here (paper optimum; >16 degrades quality). If a future spike
  wants to confirm the >16 degradation on Bifrost, bump `BufferCapacityMax` (a one-line const + a
  rebuild) — documented, not designed-for now.
- **Knob surface naming.** `bufferCapacity` (int, `[0,16]`) is proposed for BCL-ctor consistency with
  `boundedCapacity`/`stickiness`; an alternative `enableBuffering` bool is simpler but loses the
  bounded A/B-sweep dial. Resolve at plan time if the int range feels over-built.
- **Default-on flip.** Tracked against #30; out of scope for this feature's merge (ships off).
