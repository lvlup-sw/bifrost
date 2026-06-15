# CPQ Batched / Bulk Heap Interaction — Discovery Spike (go/no-go)

**Date:** 2026-06-15
**Issue:** [#27](https://github.com/lvlup-sw/bifrost/issues/27) — *cpq: batched/bulk heap interaction + specialized internal PQ (ESA 2021 §6)*
**Milestone:** #2 (CPQ — ESA 2021 parity)
**Depends on:** [#26](https://github.com/lvlup-sw/bifrost/issues/26) buffering — **merged** (`d40b3a5`, PR #40). This spike runs against the landed buffered `SubQueue`.
**Status:** Discovery spike. Output is a **decision**, not shippable code.
**Workflow:** `cpq-batched-bulk-heap-spike` (discovery)

> **Measurement provenance (chosen scope).** The issue's checkbox asks to *"prototype bulk-refill
> (Floyd heapify) vs per-element refill on the buffered `SubQueue`."* This spike does **not** build that
> prototype, because the gate is decided upstream of a benchmark: the specialized batch-aware internal
> PQ that #27 reaches for was **already implemented and measured by the algorithm's own authors** and
> found to be a wash, the issue's asymptotic justification (Floyd-`O(k)` refill) is an **algorithmic
> category error**, and the .NET cost model makes the residual upside *smaller* than the C++ literature,
> not larger. The verdict is therefore reached from (a) Williams & Sanders' published preliminary-
> experiment result on the exact structure, (b) asymptotic analysis cross-checked against the reference
> implementation, (c) the .NET BCL's analogous design decision in `PriorityQueue<,>`, and (d) the .NET
> write-barrier cost model. Any residual *arity-tuning* curiosity folds into the formal Xeon run
> ([#30](https://github.com/lvlup-sw/bifrost/issues/30)); it does not need its own feature. This mirrors
> the transition-bitmask spike's "Approach B NO-GO" precedent
> (`docs/research/2026-06-14-cpq-bitmask-spike.md`).

---

## TL;DR — the verdict

| Question | Verdict | Basis |
|---|---|---|
| **Does a specialized batch-aware internal PQ beat the arity-4 heap under buffering?** | **NO (directional)** | The paper's authors built it (merging binary heap) and measured it ≈ k-ary heap → rejected it (E1) |
| **Is "bulk refill via Floyd build-heap is `O(k)`" correct?** | **NO — category error** | Floyd *builds* a heap from an unordered array in `O(n)`; refill *extracts* the k smallest from an existing heap = `O(k log n)`. Floyd does not apply to refill at all (E2) |
| **Is "bulk-merge insert cheaper than k sift-ups" correct?** | **Only when `k ≳ n/log n`, which buffering deliberately avoids** | Buffering keeps `n ≫ C_I = 16`; Floyd-on-flush re-touches all `n` heap lines → anti-locality, the *opposite* of #26's goal. The .NET BCL makes exactly this call (E3) |
| **Is the win larger in .NET than the C++ paper?** | **NO — smaller** | .NET already bulk-barriers contiguous copies (`BulkMoveWithWriteBarrier`); `log₄ 16 = 2` levels leaves almost no Floyd headroom; scattered sift moves can't be bulk-barriered anyway (E4) |
| **Gate decision (#27 checkbox 1)** | **NO-GO — close or sharply rescope #27** | The §6 payoff is *cache locality*, which #26 already captured; the remaining lever is arity tuning (a knob, not a new algorithm), already decided as arity-4 and foldable into #30 (E5) |

**Recommendation: do not build the batched/specialized internal PQ.** Record the negative result and
**close or rescope [#27](https://github.com/lvlup-sw/bifrost/issues/27)** to, at most, a one-line
arity-4-vs-higher-arity A/B row inside the formal **[#30](https://github.com/lvlup-sw/bifrost/issues/30)**
Xeon run. The honest finding: *the journal paper prescribes **and measures** the batch-aware internal PQ
(merging binary heap) and rejects it as no better than the k-ary heap; the asymptotic wins #27 cites do
not exist in the `n ≫ C` regime that buffering creates; and the Floyd-`O(k)` refill claim is incorrect.*

---

## The gate question

Issue #26 (buffering) changed the internal sequential PQ's access pattern from "one heap op per queue
op" to "one heap op per `C` queue ops, in **fixed-size batches**" — flush `C_I = 16` elements *in*,
refill `C_D = 16` elements *out*. Issue #27 asks the natural follow-on: **now that the heap is only ever
touched in fixed-size batches, can the heap itself be specialized to do those batches more cheaply than
`C` independent per-element sifts?** The issue proposes two concrete mechanisms — *bulk refill via Floyd
build-heap (`O(k)`)* and *bulk insert by merging a sorted run* — and a structural one — *a specialized
internal PQ (e.g. a small sorted ring + heap hybrid)*.

The gate: **is there a real, paper-backed batched win available beyond what #26 already delivered?** The
answer is a layered **no** — and, usefully, the strongest evidence is the algorithm authors' own
measurement of exactly this idea.

---

## Evidence

### E1 — The paper already built the specialized batch-aware PQ and measured it as a wash

Williams & Sanders (journal extension arXiv:2504.11652, **§6.2 "Cache-efficient PQs"**) identify
*precisely* the structure #27 reaches for — a sequential PQ that natively consumes the fixed-size
batches buffering produces — name it, motivate it from buffering, **implement it, and measure it**:

> "To fully exploit the fact that the MultiQueue accesses the internal PQ in a bulk-fashion due to
> buffering, data structures that directly support batch operations can also be utilized. A promising
> data structure is the **merging binary heap**, an adaptation of the parallel heap […]. Merging binary
> heaps are structured like binary heaps, but **each node contains a fixed number of sorted elements.**"

And then, decisively (§6.2, verbatim, verified against the ar5iv render):

> "While merging binary heaps require fewer tree operations than k-ary heaps, they come with additional
> algorithmic complexity and higher worst-case access times. **In our preliminary experiments, merging
> binary heaps and k-ary heaps performed very similarly, so we decided to use the conceptually simpler
> k-ary heaps.**"

This is the canonical, designer-produced answer to the gate. The merging binary heap *is* the
"specialized internal PQ … tuned for batch I/O" the issue describes (node = sorted batch ⇒ a flush
merges one sorted run into one node; the structure exists specifically to exploit fixed-size batch
interaction). The authors had the buffered regime, the batch-aware structure, and a measurement
harness — and the batch-aware structure was **no faster**, so they shipped the simpler k-ary heap.
Bifrost is a faithful port of that same k-ary design; there is no reason to expect Bifrost to recover a
win the originators measured as absent.

### E2 — "Bulk refill via Floyd build-heap is `O(k)`" is an algorithmic category error

Refill extracts the `k = C_D` **smallest** elements from an *already heap-ordered* `n`-element heap into
the sorted deletion buffer `D`. Floyd's build-heap (a.k.a. heapify) is a different operation entirely: it
**constructs** a heap from an *unordered* array in `O(n)`. It does nothing to extract the k smallest from
a heap that is already ordered — you cannot "Floyd" your way to the k smallest of an existing heap.

The correct cost of extracting the k smallest from an `n`-element binary/k-ary heap is the standard pop
loop: `k` extract-mins, each `O(log_k n)` ⇒ **`O(k log n)`**, *not* `O(k)`. Both the paper and the
reference implementation do exactly this per-element loop, by construction:

- Paper (§6.1): *"refill D from Q is done by **iteratively deleting the smallest element from Q and
  inserting it into D**."*
- Reference impl (`marvinwilliams/multiqueue`, `buffered_pq.hpp`):
  ```cpp
  void refill_deletion_buffer() {
      flush_insertion_buffer();
      size_type front_slot = std::min(deletion_buffer_size, pq_.size());
      deletion_end_ = front_slot;
      while (front_slot != 0) {
          deletion_buffer_[--front_slot] = pq_.top();
          pq_.pop();                                  // per-element extract-min, each O(log_k n)
      }
  }
  ```
  The wrapped heap exposes only `push`/`pop`/`sift_up`/`sift_down` — **no `make_heap`/Floyd is present**,
  and the "we could also merge the insertion buffer and heap into the deletion buffer" idea is left as an
  **untaken comment**.

Bifrost's landed `RefillDeletion` (`SubQueue.cs` ≈L1184) is bit-faithful: a `for k < min(C, _size)` loop
of `TryHeapPop`, writing front-to-back so `D` comes out sorted. That is already the correct asymptotics
for a 16-slot sorted buffer; **there is no free Floyd-`O(k)` refill to capture.** (A genuine `O(k)`
heap-selection exists — Frederickson's — but it returns the k smallest *unsorted*, is "very
complicated," carries large constants, and needs auxiliary structures. It is irrelevant to a 16-slot
*sorted* cache-locality buffer.)

### E3 — "Bulk-merge insert" wins only in a regime buffering is built to avoid — and the BCL agrees

The second proposed win — flush `C_I` elements by *merging a sorted run* / Floyd-rebuilding instead of
`C_I` sift-ups — is directionally real but only in a regime buffering deliberately keeps you out of.
Inserting `k` elements into an existing `n`-element heap costs `O(k log n)` by sift-ups, or `O(n + k)` by
"append k then Floyd-heapify the whole array." **Bulk-rebuild beats sift-ups only when `k ≳ n / log n`** —
i.e. when the heap is small or empty *relative to the batch*. Buffering's entire purpose is the opposite:
the heap accumulates the bulk of the elements while the 16-slot buffers skim the top, so a flush of
`C_I = 16` lands in a heap that is typically **much larger than 16** (`n ≫ k`). In that regime
per-element sift-up is the cheaper path, and **Floyd-rebuilding the whole `n`-element heap on every
16-element flush would re-touch all `n` heap cache lines** — actively *anti-locality*, the exact opposite
of what #26 set out to achieve.

The .NET BCL makes precisely this judgment in the analogous structure. `System.Collections.Generic.PriorityQueue<TElement,TPriority>`
is the same arity-4 `(element, priority)` implicit heap as Bifrost's `SubQueue`. Its `EnqueueRange`
re-heapifies (bottom-up Floyd `Heapify()`) **only when the queue is empty** (`_size == 0`); when the heap
already holds elements it **falls back to per-element `Enqueue` (sift-up)** with no re-heapify. A flush
(16 into a typically non-empty heap) is exactly the non-empty case where the BCL authors chose
per-element sift over bulk-rebuild. Bifrost's `FlushInsertion` (`SubQueue.cs` ≈L1165, per-element
`HeapPush` loop) already matches that decision.

### E4 — In .NET the win is *smaller* than the C++ paper, not larger

A reasonable steelman for revisiting the paper's wash is "the .NET cost model differs — write barriers on
ref-type element moves change the calculus." It does change it, but in the **wrong direction** for #27:

1. **.NET already bulk-barriers contiguous copies.** For an array/inline-buffer of a GC-containing type
   (`(TElement ref, TPriority)`), `Array.Copy` / `Span<T>.CopyTo` do **not** emit one `JIT_WriteBarrier`
   per element — they detect GC pointers and route the whole region through a single
   `Buffer.BulkMoveWithWriteBarrier`, which copies the bytes and does *imprecise, bulk* card-marking
   offloaded to the GC pause (jkotas, dotnet/runtime#93288; EgorBo PR#101761 measured per-field→bulk
   barrier elimination at 0.45–0.64×). So the per-element-barrier penalty the C++ batch-PQ work
   optimizes against is **largely already gone** in .NET for any contiguous copy.
2. **The heap is too shallow for Floyd to matter.** `log₄ 16 = 2` levels. Floyd's `O(n)`-vs-`O(n log n)`
   edge is an asymptotic statement that barely materializes at depth 2; the constant-factor move
   reduction over a 16-element arity-4 heap is tiny, and each sifted move is *already* a single
   bulk-eligible tuple store.
3. **The expensive part can't be bulk-barriered regardless.** Only the *contiguous staging* copies
   (buffer→heap-tail, heap→sorted-buffer) are bulk-barrierable. The actual heap re-ordering — sift-down's
   scattered parent/child shuffles — is non-contiguous and gets per-store barriers whether or not you
   "specialize." Specialization cannot touch the dominant term.

The one defensible .NET-flavored hypothesis (stage a contiguous batch to shorten the locked critical
section) is *already* realized by #26: refill writes `D` contiguously front-to-back, flush drains a
contiguous `I`. There is no additional contiguity for a specialized PQ to extract.

### E5 — #26 already captured §6's real payoff; the remainder is a tuning knob, not an algorithm

§6's measured benefit is **cache locality** — touch the deep heap only when buffers fill or empty, with
high temporal locality — which is exactly what #26's insertion/deletion buffers deliver (and what the #26
spike, `docs/research/2026-06-15-cpq-buffering-spike.md`, identified as the GO). The only remaining §6.2
lever is **arity**: `k`-ary heaps cut cache misses to `O(log_k n)` "if `k` elements fit into one cache
line." That is a one-parameter tuning knob, not the "specialized internal PQ / bulk Floyd refill" feature
#27 describes — and Bifrost has *already* made the deliberate, write-barrier-grounded choice to run
**arity-4** rather than the reference impl's arity-8 (halving barrier-bearing element moves per sift on
.NET; see `BACKGROUND.md`). Re-opening arity is a single benchmark row, and it belongs in the formal Xeon
A/B (#30), not a new feature workflow.

---

## What a Bifrost prototype would look like — and why it does not pay

For completeness, the two #27-proposed mechanisms mapped onto the landed code, with the reason each is a
non-starter:

| Proposed mechanism | Where it would land | Why it does not pay |
|---|---|---|
| **Floyd-`O(k)` bulk refill** | replace the `TryHeapPop` loop in `RefillDeletion` (`SubQueue.cs` ≈L1184) | Category error (E2): refill *extracts* the k smallest; Floyd *builds*. No `O(k)` sorted extraction exists. The per-element loop is already correct-asymptotics. |
| **Bulk-merge flush** (Floyd-rebuild / sorted-run merge) | replace the `HeapPush` loop in `FlushInsertion` (`SubQueue.cs` ≈L1165) | Wins only at `k ≳ n/log n` (E3); buffering keeps `n ≫ 16`, so sift-up wins and Floyd-rebuild re-touches all `n` lines (anti-locality). The BCL declines this exact case. |
| **Specialized batch-aware internal PQ** (merging binary heap / sorted-ring+heap hybrid) | a new `internal` PQ type behind `SubQueue`'s heap | The paper built and measured this and it was a wash (E1); .NET's bulk barriers + shallow heap shrink the upside further (E4); cost is "additional algorithmic complexity and higher worst-case access times" (the paper's own words) for ≈0 throughput. |

The prototype would be straightforward to write (the heap, buffers, and write-barrier-gated moves all
already exist), AOT-safe, and 0 B/op — none of those are the obstacle. The obstacle is that the expected
throughput delta is **≈0**, established by the originators' own measurement, with negative expected value
once "additional algorithmic complexity and higher worst-case access times" is priced in.

---

## If anyone insists on building it anyway — the risks to price in

1. **Negative expected value on throughput.** The headline finding (E1): the authors measured the
   batch-aware PQ as ≈ k-ary and rejected it for simplicity. Building it is paying complexity for a
   measured wash.
2. **Worse worst-case latency / quality.** The paper flags merging heaps' "higher worst-case access
   times." That feeds operation-latency variance, which perturbs MultiQueue load balance and **degrades
   rank error** — Bifrost would have to re-prove its rank-error gates (mean ≤ 2·(5/6)·n·s, P99 ≤ 10·n·s),
   for no throughput gain.
3. **More write-barrier surface, not less.** A merging heap moves *sorted batches* between nodes; on
   reference `TElement` those are more element moves under the lock, partially offset by bulk-barrier
   routing (E4) but not a net reduction at `k = 16`.
4. **Arity is the only live knob, and it's already chosen.** Any real tuning is arity-4 vs higher arity
   under the buffered regime — one A/B row, folds into #30, and the existing arity-4 choice is
   write-barrier-grounded.

## Residual lever (the only thing worth doing) → fold into #30

Not a feature: a single **arity-4-vs-arity-8 (and -16) A/B row** under the buffered regime
(`bufferCapacity = 16`), measured on the Xeon 8573C alongside the already-planned #30 baseline. Expected
outcome per E4/E5: arity-4 stays best on .NET (write-barrier-bound element moves dominate the cache-miss
reduction the paper's `k = 8` buys). If that surprises, it's a one-line constant change, not a new
internal PQ. **No `perf`/Floyd prototype required.**

---

## Verdict

**NO-GO (directional).** A specialized batch-aware internal PQ does not beat Bifrost's arity-4 heap under
buffering — the algorithm's own authors implemented the merging binary heap, measured it as "very
similar" to the k-ary heap, and rejected it for simplicity (E1). The issue's specific justifications do
not hold: Floyd-`O(k)` bulk refill is a category error (Floyd builds, refill extracts; per-element
`O(k log n)` is already correct, E2), and bulk-merge flush wins only at `k ≳ n/log n`, a regime buffering
is purpose-built to avoid — the .NET BCL makes the identical per-element-over-bulk-rebuild call for a
non-empty heap (E3). In .NET the upside is *smaller* than the C++ literature, not larger, because
contiguous moves are already bulk-barriered and a 16-element arity-4 heap is only two levels deep (E4).
§6's actual payoff — cache locality — was already captured by #26, and the only remaining lever (arity)
is a tuning knob Bifrost has already resolved as arity-4 (E5).

**Next step:** record the negative result on [#27](https://github.com/lvlup-sw/bifrost/issues/27) and
**close or sharply rescope** it to a single arity A/B row inside
[#30](https://github.com/lvlup-sw/bifrost/issues/30). No `/exarchos:ideate` escalation; there is no
feature to build. This is a legitimate research outcome — the spike's value is preventing a feature the
originators already proved worthless, mirroring the transition-bitmask "Approach B NO-GO."

---

## References

1. Williams, Sanders. *Engineering MultiQueues: Fast Relaxed Concurrent Priority Queues* (journal
   extension). arXiv:2504.11652, **§6.1 (Buffering)**, **§6.2 (Cache-efficient PQs — merging binary heap
   measured ≈ k-ary, rejected)**. ACM TOPC [10.1145/3771738](https://dl.acm.org/doi/10.1145/3771738).
   Read via the ar5iv HTML mirror; §6.2 decision sentence verified verbatim.
2. Williams, Sanders, Dementiev. *Engineering MultiQueues.* ESA 2021, LIPIcs 204 (conference version).
   arXiv:2107.01350; [DROPS](https://drops.dagstuhl.de/entities/document/10.4230/LIPIcs.ESA.2021.81).
3. Reference implementation: `github.com/marvinwilliams/multiqueue`,
   `include/multiqueue/buffered_pq.hpp` (per-element `refill_deletion_buffer`/`flush_insertion_buffer`,
   buffers 16/16, "we could also merge" left untaken) + `include/multiqueue/heap.hpp` (arity-8 default,
   `push`/`pop`/`sift_*` only — no Floyd/`make_heap`).
4. .NET BCL precedent: `dotnet/runtime` `PriorityQueue.cs` — arity-4 tuple heap; bottom-up `Heapify()`
   used **only** when `_size == 0`; `EnqueueRange` falls back to per-element sift-up into a non-empty
   heap.
5. .NET write-barrier cost model: `Array.Copy`/`Span.CopyTo` over GC types → single imprecise
   `BulkMoveWithWriteBarrier` (dotnet/runtime#93288, jkotas; PR#101761, EgorBo); BOTR GC write-barrier
   doc.
6. Bifrost source map (post-#26 merge `d40b3a5`): `src/Bifrost.Concurrency/{SubQueue.cs,
   SubQueueBuffer.cs}` — `FlushInsertion` ≈L1165, `RefillDeletion` ≈L1184, arity-4 heap (children
   `4i+1..4i+4`), six `IsReferenceOrContainsReferences` write-barrier gate sites; `BACKGROUND.md`
   (arity-4 rationale).
7. Bifrost design + precedent: `docs/designs/2026-06-15-cpq-buffered-multiqueue.md` (#26, DR-1..DR-8);
   `docs/research/2026-06-15-cpq-buffering-spike.md` (#26 GO spike);
   `docs/research/2026-06-14-cpq-bitmask-spike.md` (Approach-B NO-GO precedent, methodology).
