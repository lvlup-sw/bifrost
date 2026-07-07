# The Concurrent Priority Queue .NET Never Shipped — Substack outline

**Working titles (number-first, pick one):**
- *118 Million Ops/Sec: .NET's First Scalable Concurrent Priority Queue*
- *The Concurrent Priority Queue .NET Never Shipped*
- *The Missing Shelf: Building a Scalable Concurrent Priority Queue for .NET*

**Spine:** you can't have one → because strict can't scale → so relax it → here's the .NET build → here are the numbers.

**Voice:** von Geijer — intuition before mechanism, figures carry the argument, caveats stated flat. One idea per line.

---

## 1. Hook — the missing shelf in the concurrent toolbox

- Multicore era: performance comes from parallelism now, not clock speed. Software has to actually use the cores.
- Concurrent data structures are how you do that — the shared building blocks under every parallel workload.
- .NET ships a solid set: `ConcurrentQueue` (FIFO), `ConcurrentStack` (LIFO), `ConcurrentBag` (unordered).
- The pattern: every one is *no ordering* or *trivial ordering*. Need **priority** ordering and the shelf is empty.
- The tell: `PriorityQueue<T>` exists — and shipped *deliberately* not thread-safe. No concurrent counterpart in the BCL, or the ecosystem.
- The claim, flat: this is the only scalable concurrent priority queue in .NET.
- *→ of all the concurrent collections, why is priority the one nobody shipped?*

## 2. Why a lock is the ceiling

- Wrap `PriorityQueue` in a lock → correct, trivial, **flat at 5–8M ops/s no matter the core count.**
- Not lazy code — the *spec*: every consumer fights over the one smallest element. A serial point by definition.
- Lock-free doesn't save you — the head is still the bottleneck.
- That's why .NET shipped the other three and skipped this one. Priority is the hard one.
- *→ how do you scale past a point that's serial by definition?*

## 3. Stop asking for *the* smallest

- The move: n small heaps, each its own lock, each publishing its top so any thread can read it without locking.
- Insert → drop into a random heap. Pop → **peek two at random, take the better one.**
- The intuition (the beautiful part): two-choice sampling self-stabilizes — pops chase whichever heap ran ahead, so the tops stay clustered. Power-of-two-choices, same math as balls-in-bins.
- One choice isn't enough — rank error grows unbounded. Two makes it finite. That's the whole trick.
- The price, named: you get *a* smallest, not *the* smallest. Rank error ≈ (5/6)·n. It scales with the *machine* (n ≈ 4×cores), not the workload.
- Credit: this is the **MultiQueue** (Rihani/Sanders/Dementiev '15; Williams et al. '21; exact bound Walzer/Williams '25).
- *→ nice on paper — what does it take to make it a real .NET type?*

## 4. Building it in .NET

- The papers are C++. Porting the idea into a zero-alloc, AOT-safe .NET type is where the real work is.
- Heaps: arity-4, mirroring the BCL `PriorityQueue` layout — familiar, and it makes the lock baseline an apples-to-apples rival.
- Never block: a failed try-lock resamples a different heap instead of waiting. Sizing n to 4×cores guarantees a free heap always exists — expected lock acquisitions per op barely above one.
- Lock-free peek = a seqlock: torn-read safety under a concurrent writer via version stamps + memory barriers. (The arm64 store-store reordering gotcha — good aside.)
- Per-thread xoshiro256\*\* for sampling; power-of-two n turns heap selection into a mask, not a modulo.
- Zero-alloc + AOT, because it's a library others ship: `[InlineArray(16)]` buffers, cache-line padding, devirtualized comparer paths. **0 B/op, value *and* reference elements.**
- Three ESA-2021 optimizations, one line each: **buffering** (touch the deep heap rarely; default-off), **stickiness** (reuse a heap for locality), **occupancy bitmask** (route around empty heaps instead of scanning).
- The strict escape hatch: `TryDequeueMin` scans all n tops in O(n) for the true minimum — in the API precisely because relaxation scales with core count.
- *→ does all this actually beat the lock?*

## 5. The numbers

- Setup line: 64-thread Xeon 8573C, .NET 10, split-workload methodology (built to expose weak relaxed designs), vs. a single global lock around a heap.
- Headline: **monotonic climb to ~118M ops/s @ 64T; the lock is flat at 5–8M from one thread on. 12–25× by 64 threads, parity crossed at just 4.** [Fig 1, 4]
- Scaling quality: ~1.7× per thread-doubling (~85% efficiency) through 32T, easing to 1.4× at 64T — coherence/bandwidth bound, not a defect. [Fig 5]
- The relaxation cost, shown not hidden: rank-error curve vs cores (top ~27 on an 8-core box, top ~213 here). The price for the line above. [Fig 7]
- The honest tax: single-thread 1.4–2.2× a lock-wrapped `PriorityQueue`, 0 B/op → single-threaded code should keep using `PriorityQueue`, and the xmldoc says so. It's a *contention* win. [Fig 6]
- Optimization payoffs, one line each: stickiness 10.6→20.2M ops/s @2T [Fig 2–3]; bitmask 264→146 ns (1.81×) on sparse pops [Fig 8]; buffering −31% on deep reference-type latency but workload-dependent [Fig 9–10]; **arity-8 flips the winner on server silicon** — the surprise [Fig 11].
- *→ so where does this go?*

## 6. Closer — forward-pointing

- One sentence: the only .NET priority queue whose throughput *rises* with your core count.
- It wants to be a BCL proposal — the gap is real, the bar (beat a lock) is cleared.
- CTA: the algorithm walkthrough + open benchmark repo; go read von Geijer's MultiQueue intro for the theory.

---

## Drafting notes

- **Length:** this runs long for one post (~2,500+ words with figures). Natural split point is after §3 — "the idea" as part 1, "the .NET build + numbers" as part 2 — if you want two installments.
- **Figures do the work in §5** — lift them straight from `docs/benchmarks/2026-06-13-cpq-xeon-8573c.md`; each already has a plain-language caption.
- **Citations to weave (footnote style, like the proposal draft):** MultiQueue SPAA'15, Engineering MultiQueues ESA'21, exact rank error ESA'25, power-of-choice PODC'17.
- **Write the headline last** (research consensus); pick it once the opener's on the page.

## Source material

- API framing + demonstrated-need + claim wording: `docs/proposals/draft.md`
- All benchmark numbers and figures: `docs/benchmarks/2026-06-13-cpq-xeon-8573c.md`
- Inspiration (voice/structure): von Geijer, *MultiQueue: The Relaxed Priority Queue* — https://karevongeijer.com/blog/multiqueue-introduction
