# CPQ Buffered MultiQueue — Discovery Spike (go/no-go)

**Date:** 2026-06-15
**Issue:** [#26](https://github.com/lvlup-sw/bifrost/issues/26) — *cpq: buffered MultiQueue — per-sub-queue insertion + deletion buffers (ESA 2021 §4)*
**Milestone:** #2 (CPQ — ESA 2021 parity)
**Status:** Discovery spike. Output is a **decision + design**, not shippable code. If the verdict is GO, the production buffered `SubQueue` is built later under TDD (escalate `/exarchos:ideate` → `/exarchos:plan`).
**Workflow:** `cpq-buffered-multiqueue-spike` (discovery)

> **Measurement provenance (chosen scope).** The issue's first checkbox asks for `perf stat`
> cache-misses/op on the dense path on the Xeon 8573C. `perf` is not installed on the dev host and
> the host is an i9-13900K (Raptor Lake, AVX-512 fused off), not the Xeon. Per the user's scope
> decision, this spike answers the gate **directionally** from (a) the algorithm authors' own
> published cache-miss measurements, (b) Bifrost's *already-committed* Xeon single-thread latency
> curve, and (c) static cache-line accounting of Bifrost's actual hot path. The **formal**
> `perf`-stat A/B (cache-misses/op down, throughput up, with the real prototype, at n=256/64T) is
> **deferred to [#30](https://github.com/lvlup-sw/bifrost/issues/30)**. This mirrors the
> transition-bitmask precedent (`docs/research/2026-06-14-cpq-bitmask-spike.md`), whose absolute
> numbers were likewise deferred to the Xeon.

---

## TL;DR — the verdict

| Question | Verdict | Basis |
|---|---|---|
| **Is the dense MultiQueue path locality-bound?** | **YES (directional)** | Three independent lines of evidence converge (E1–E3 below) |
| **Gate decision (§26 checkbox 1)** | **GO** | Locality-bound confirmed; buffering is the paper's *primary* fix for exactly this |
| **Is it "quality-neutral" as the issue claims?** | **NO — approximately neutral at C≤16 only** | Paper measures quality degradation past C=16; the cost is indirect (flush/refill latency variance), not zero. Must be A/B-gated against the rank-error contract, not assumed. |
| **Keep Bifrost's arity-4 heap, or switch to the paper's k=8?** | **KEEP arity-4** | Paper: arity has only minor throughput impact; Bifrost's k=4 is a deliberate GC-write-barrier choice on .NET that the paper's C++ measurements don't price in |
| **Recommended buffer sizes** | **C_I = C_D = 16** | The paper's tuned optimum (largest size before quality degrades) |

**Recommendation: proceed to a production TDD effort** for a buffered `SubQueue` (insertion buffer
`I`, sorted deletion buffer `D`, both cap 16), preserving the seqlock-published top, the
verification-scan false-contract, and the occupancy-bitmask routing. **Gate the merge on a Xeon A/B**
(throughput up, `0 B/op` preserved, rank-error gates green) folded into #30. Do **not** assume the
quality-neutrality the issue asserts — measure it.

---

## The gate question

Issue #26 is *measurement-gated*: buffering is only worth building if the dense path is
**locality-bound** (most per-op cost is cache-line accesses to the deep heap → buffering, which keeps
the hot path inside small contiguous buffers, removes those accesses). If instead the path is
**bandwidth/coherence-bound** (saturating memory bandwidth or bouncing contended cache lines between
cores), buffering changes neither and the issue says *stop and close*.

The honest answer below is **two-layered**, because the MultiQueue has two distinct cost regimes:

- the **per-operation floor** (what one op costs with no contention), and
- the **multi-thread scaling slope** (how throughput grows with cores).

Buffering attacks the first directly and the second indirectly. Both point to GO.

---

## Evidence

### E1 — The algorithm's authors measured it: the MultiQueue is cache-miss-limited

Williams & Sanders (journal extension arXiv:2504.11652 §6.1; ESA 2021 §4; ACM TOPC
[10.1145/3771738](https://dl.acm.org/doi/10.1145/3771738)) introduce buffering precisely to fix
locality, and **measure the correlation directly**:

> "To reduce the average number of cache lines accessed by the insert and delete operations, we
> enhance each internal PQ with an insertion buffer and a deletion buffer of fixed capacity. […]
> **Figure shows a clear correlation between buffer size, throughput, and cache misses. The
> correlation is in line with our expectations that the performance of the MultiQueue is limited by
> cache misses, and confirms that buffering is an effective mitigation measure.**"

Their parameter sweep evaluated buffer sizes `C ∈ {0, 4, 16, 64, 256, 1024}` and heap arities
`k ∈ {2, 4, 8, 16}`, landing on **C = 16, k = 8** as the throughput optimum that does not sacrifice
quality (larger `C` raises throughput but degrades quality through higher operation-latency variance;
`C > 256` barely helps throughput while hurting quality materially).

This is the canonical, peer-reviewed answer to the gate question, produced by the algorithm's
designers on real hardware. Bifrost's `ConcurrentPriorityQueue` is a **faithful port of the same
algorithm** with the same defining access pattern — *sample two random sub-queues, lock one, pop its
heap* — so the locality property is inherited by construction, not assumed.

### E2 — Bifrost's own committed Xeon latency curve shows the locality signature

`docs/benchmarks/2026-06-13-cpq-xeon-8573c.md`, **Figure 6** (single-thread Enqueue+Dequeue pair,
0 B/op, n = 256):

| Population (heap depth) | Relaxed CPQ | Lock-wrapped `PriorityQueue` |
|---|---|---|
| 1 k | **80 ns** | ~57 ns |
| 100 k | ~ (rising) | ~ |
| 1 M | **176 ns** | ~89 ns |

The relaxed queue's pair latency **rises 80 → 176 ns as the heap deepens** from 1 k to 1 M elements
(≈5 extra arity-4 levels). This measurement is **single-threaded**: there is *no lock contention and
no cross-core coherence traffic*. The 2.2× latency growth is therefore **pure data-access cost** —
the sift-down walking more cold heap levels as the heap deepens. That isolates the locality component
of per-op cost and shows it is the dominant, depth-scaling term. The i9 transition-bitmask spike is
consistent (steady pop-1000 ≈ 88 ns, matching the Xeon's 80 ns @ 1 k).

**Buffering removes exactly this term from the hot path**: a delete pops `D.front()` (one contiguous
cache line) and touches the deep heap only once per `C_D` deletes (refill). So the 80 → 176 ns
depth-driven growth is precisely what a deletion buffer flattens.

### E3 — Static cache-line accounting of the dense path

From the source map of `src/Bifrost.Concurrency/` (SubQueue.cs, ConcurrentPriorityQueue.Dequeue.cs):

A dense **dequeue** touches, per op:

- **lock-free sampling of 2 random sub-queues** — for each: `TopVersion` (`SubQueueHeader.cs:78`,
  cache line 2) read twice, the padded cached-top slot (`PaddedTopSlot`, its own isolated line),
  `EmptyFlag` (`SubQueueHeader.cs:86`, line 4). ≈ 6–8 lines, and the two sub-queues are chosen by
  the xoshiro RNG → **effectively random → cold** (working set ≫ L1/L2).
- **the winning pop under the lock** (`SubQueue.TryLockedPop` → `PopHeldRoot` → `TryHeapPop`,
  SubQueue.cs:702–767): the lock word, `_nodes[0]`, then the **sift-down across `log₄(N)` heap
  levels** — each level a near-random array offset → cold line — plus `_size`, the seqlock republish
  (`PublishTop`, SubQueue.cs:227–256), `Count`, and an occupancy bit only on an emptiness boundary.

The **deep-heap sift is the dominant cold-line source** and the one that grows with population
(matching E2). Insertion buffer + deletion buffer convert "touch the cold heap every op" into "touch
a 1–2-line contiguous buffer every op; touch the heap once per `C` ops." With `C = 16` that is a
~16× reduction in heap interactions, **independent of stickiness** (see E4).

### E4 — The amortization holds even at Bifrost's default stickiness s = 1

A natural objection: buffering's classic payoff is consecutive ops on the *same* sub-queue hitting
the buffer — but Bifrost ships `s = 1` (each op samples two *fresh* random sub-queues), so a given
sub-queue is touched only ~once per `n/2` ops. Does the buffer still help?

**Yes, because the amortization is per-sub-queue, not per-consecutive-op.** Each sub-queue's deletion
buffer is refilled with `C_D` elements in a single heap interaction; the next `C_D − 1` deletes from
that sub-queue — *whenever they happen, however widely spaced* — are pure buffer reads with no heap
touch. Over the queue's lifetime, only `1/C_D` of deletes hit the heap, regardless of `s`. Stickiness
and buffering are **orthogonal** locality wins (stickiness amortizes RNG + sampling across ops;
buffering amortizes heap access within a sub-queue). The paper's headline numbers are themselves at
its default stickiness, not a high-`s` regime.

### The honest counter-consideration (why this is still GO, and the claim's boundary)

The same Xeon doc, Figure 5 caption, says the multi-thread scaling curve "stays globally sublinear,
**bounded by coherence and memory bandwidth**." At 32 → 64 threads the *scaling slope* is limited by
cross-core traffic, not by single-op locality. If the dense regime were *purely* bandwidth-bound,
buffering would not move it — that is the STOP scenario the issue warns about.

It is not the STOP scenario, for two reasons:

1. **The per-op floor is locality-bound (E2, single-thread, contention-free).** Lowering that floor
   raises throughput at *every* thread count; buffering's win is to **raise the absolute ceiling**,
   which is exactly the outcome issue #26 itself predicts ("buffering would raise the absolute ceiling
   rather than fix a scaling break").
2. **Buffering shortens the critical section.** Today a pop holds the sub-queue lock across a full
   `log₄(N)` sift-down; with a deletion buffer it holds the lock across a single decrement + read on
   `C_D − 1` of `C_D` pops. Shorter hold → less lock-line contention → *less* coherence pressure at
   high thread count. So buffering also nudges the bandwidth/coherence regime in the right direction;
   it just does not reduce the lock-*acquisition* count (one try-lock per pop, unchanged).

**Boundary of the claim:** this spike asserts locality-boundedness of the *per-op floor*
directionally. It does **not** quantify the multi-thread throughput delta — that is the formal
Xeon A/B (#30). A GO here means "the mechanism applies and the evidence is strong enough to build the
prototype," not "the win is N%."

---

## The reference algorithm (canonical, from `marvinwilliams/multiqueue` `buffered_pq.hpp`)

Each internal PQ is wrapped as `BufferedPQ<PQ, C_I, C_D>` with:

- **insertion buffer `I`** — unsorted, capacity `C_I`.
- **deletion buffer `D`** — *sorted*, capacity `C_D`, holding the current `C_D` smallest elements;
  its minimum (`D.front()`) is the structure's `top()`.
- the underlying **heap `pq_`**.

Rules (exactly as implemented):

- **`top()`** = `D.front()` (the smallest buffered element).
- **`pop()`**: remove `D.front()`; **if `D` is now empty, `refill`**.
- **`push(v)`**:
  - if `D` non-empty and `v ≤ max(D)` → **insert `v` into `D` in sorted order**; if `D` was full,
    evict `max(D)` into `I` (or flush `I`→heap then heap-push it, if `I` is also full).
  - else if `D` not full and heap & `I` are empty → insert `v` directly into `D` (keeps tiny
    structures entirely in the buffers).
  - else → append `v` to `I`; **if `I` is full, flush `I`→heap first**, then heap-push `v`.
- **`flush(I)`**: push every `I` element into the heap; `I` empty.
- **`refill(D)`**: `flush(I)`; then pop the `min(C_D, heap.size)` smallest from the heap into `D`
  (sorted).
- **Invariant (load-bearing):** `D` empty ⟺ `I` empty *and* heap empty ⟺ **the whole sub-queue is
  empty**. (In the C++: `assert(deletion_end_ != 0 || (insertion_end_ == 0 && pq_.empty()))`.)

The heap is therefore touched **only** on `I`-full flush or `D`-empty refill — the locality win.

---

## Bifrost prototype sketch (if GO — for the ideate/plan stage)

Target: `internal sealed class SubQueue<TElement, TPriority>` (SubQueue.cs:43). It is a class with
normal managed layout (no fixed-layout obstacle), so new fields are safe.

**New fields** (cold — touched only under the existing `SyncLock`, never on the lock-free read path):

```csharp
private (TElement Element, TPriority Priority)[] _insertionBuffer; // cap C_I (16)
private int _insertionCount;
private (TElement Element, TPriority Priority)[] _deletionBuffer;  // cap C_D (16), sorted; min published
private int _deletionCount;
```

**What changes, and what must NOT:**

| Concern | Today | With buffering | Contract preserved? |
|---|---|---|---|
| Published top (`PublishTop`, SubQueue.cs:227) | heap root | `D.front()` | **Yes** — seqlock unchanged; top still a single `TPriority`+empty flag. Republish on every pop / front-changing push / refill (same frequency as today). |
| Lock-free read (`TryReadTop`, SubQueue.cs:278; sampled at Dequeue.cs:177) | reads published top | unchanged — reads `D.front()` via the same slot | **Yes** — reader never sees buffers |
| Emptiness (`EmptyFlag`) | heap empty | `D` empty (⟺ whole sub-queue empty, by invariant) | **Yes, and cleaner** — exact local empty signal |
| Occupancy bit (`Set/ClearOccupancyBit`, SubQueue.cs:832/846) | heap 0↔1 boundary | `D` 0↔non-0 boundary | **Yes** — same atomic-on-boundary discipline |
| Verification scan (`TryDequeueVerificationScan`, Dequeue.cs:324) | sole `false` authority | unchanged — reads published top/empty | **Yes** — buffers invisible to the scan |
| `0 B/op` | yes | yes — buffers are fixed arrays allocated once per sub-queue at construction | **Yes** |

The `D`-empty ⇒ empty invariant is the cheap **local** emptiness signal the issue's "synergy" note
anticipates; it complements (does not replace) the global occupancy bitmask.

---

## Bifrost-specific risks to engineer (the real work)

1. **GC write barriers on buffer moves (the issue's headline .NET caveat).** Sorted-insert into `D`,
   eviction of `max(D)`, flush `I`→heap, and refill heap→`D` all *move* `(TElement, TPriority)`
   tuples. For reference `TElement` (e.g. the orchestrator's work envelope with a `long` virtual-time
   priority), each move is a write barrier. Bifrost already gates heap clears on
   `RuntimeHelpers.IsReferenceOrContainsReferences<T>()` (SubQueue.cs:384, 789); the batched buffer
   moves must follow the same gating and prefer block copies. For value-type-only `TElement` the
   moves are pure memcpy (zero barriers) — the maximal-win case (and the `int` benchmark path).
   **Do not frame buffering as a barrier *reduction*** — refill/flush do `C` heap ops in a batch;
   the win is *cache-line* locality, and barriers are a cost to *contain*, not a benefit.

2. **Sorted-insert is O(C_D) under the lock.** `push(v ≤ max(D))` scans + shifts up to `C_D`
   elements. For Bifrost's dominant near-monotonic virtual-time keys, new pushes are usually *larger*
   than `max(D)` → they hit the O(1) insertion-buffer append, so the O(C) path is rare. Adversarial
   decreasing-key streams make it hot; `C = 16` bounds it. Verify with a decreasing-key A/B.

3. **Quality is NOT exactly neutral (correct the issue's premise).** The published top stays an exact
   per-sub-queue minimum, so two-choice *comparisons* are unchanged — but the paper measures rank
   error / delay *deteriorating* for `C > 16`, because occasional expensive flush/refill ops inject
   high-variance latency that perturbs load balance. At `C ≤ 16` it is negligible *in their setup*.
   Bifrost must verify it against its own rank-error gates (suite's mean ≤ 2·(5/6)·n·s,
   P99 ≤ 10·n·s) in the A/B — **not assume neutrality.**

4. **Keep arity-4, do not adopt the paper's k=8.** The paper found arity a minor knob and picked
   k=8; Bifrost deliberately runs k=4 to halve write-barrier-bearing element moves per sift on .NET
   (BACKGROUND.md §"What the .NET implementation does differently"). That reasoning is unaffected by
   buffering. Add `C=16` buffers; leave the heap arity alone.

5. **Test-hook instrumentation.** Follow the established `BIFROST_TEST_HOOKS` pattern (fields +
   `…ForTest` getters ungated so the stripped publish build still compiles; increment *sites* gated)
   for buffer-flush / refill / buffered-hit / buffered-pop counters, mirroring the bitmask feature's
   `_debugOccupancyWriteCount` (SubQueue.cs:88–92, 864–869).

---

## A/B plan (for the implementation gate; formal run → #30)

Build the buffered `SubQueue` behind a construction flag (or a parallel type) so before/after is a
toggle, then measure:

- **Dense throughput** (UniformMixed 50/50), threads 1→64, n=256 — *primary*: expect the absolute
  ceiling to rise; the win should grow with heap depth / population.
- **Single-thread latency vs population** (the Fig-6 sweep, 1k→1M) — expect the 80→176 ns
  depth-driven slope to flatten.
- **`perf stat` cache-misses/op** on the dense harness, before/after (the issue's literal checkbox) —
  **Xeon only, #30** (no `perf`/AVX-512 locally). Expect misses/op to fall with `C`.
- **`0 B/op`** preserved (`[MemoryDiagnoser]`).
- **Rank-error gates** unchanged (mean / P99 bounds) — the quality guard from risk #3.
- **Full `Bifrost.Tests.Concurrency` green in Release.**

GO/NO-GO at merge: throughput up at scale **and** rank-error gates green **and** `0 B/op`. If
throughput is flat (locality win not realized in .NET as predicted) or rank error regresses past the
gate, NO-GO and close #26 with the negative result recorded.

---

## Verdict

**GO (directional).** The dense MultiQueue path is locality-bound at the per-op floor — confirmed by
the algorithm authors' direct cache-miss measurements (E1), Bifrost's own contention-free Xeon
latency curve rising with heap depth (E2), and static cache-line accounting (E3) — and the win
amortizes even at `s=1` (E4). Buffering (`C_I = C_D = 16`, arity-4 retained) is the paper's primary
remedy for exactly this and slots cleanly behind Bifrost's seqlock top, verification-scan
false-contract, and occupancy-bitmask routing without changing any of those contracts.

**Next step:** escalate to `/exarchos:ideate` → `/exarchos:plan` for the buffered `SubQueue`,
carrying the four risks above (write barriers, O(C) sorted-insert, quality-not-neutral, keep arity-4)
as explicit design constraints, and the A/B plan as the merge gate with the formal `perf` run folded
into #30.

---

## References

1. Williams, Sanders, Dementiev. *Engineering MultiQueues: Fast Relaxed Concurrent Priority Queues.*
   ESA 2021, LIPIcs 204, **§4 (Buffering)**. arXiv:2107.01350.
2. Williams, Sanders. *Engineering MultiQueues* (journal extension). arXiv:2504.11652, **§6.1
   (Buffering)** + parameter-tuning section. ACM TOPC [10.1145/3771738](https://dl.acm.org/doi/10.1145/3771738).
3. Reference implementation: `github.com/marvinwilliams/multiqueue`,
   `include/multiqueue/buffered_pq.hpp` (canonical `BufferedPQ`).
4. Bifrost source map: `src/Bifrost.Concurrency/{SubQueue.cs, ConcurrentPriorityQueue.Dequeue.cs,
   SubQueueHeader.cs, PaddedTopSlot.cs}`; `src/Bifrost.Concurrency/BACKGROUND.md`.
5. Bifrost benchmarks: `docs/benchmarks/2026-06-13-cpq-xeon-8573c.md` (Fig. 5 scaling bound, Fig. 6
   latency-vs-depth).
6. Precedent spike (methodology, directional-on-i9 / defer-to-Xeon): `docs/research/2026-06-14-cpq-bitmask-spike.md`.
