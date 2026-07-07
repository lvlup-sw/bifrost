Somewhere in the mid-2000s, processor clock speeds stopped climbing. Individual cores were bumping into hard physical limits on power and heat, so instead of getting faster, chips got wider: more cores, not quicker ones. For instance, the clock speed on my current desktop is barely ahead of the one I had in college; the difference is that it now has 24 cores. Making software *fast* has turned into making software *parallel*, and parallel code is built on a handful of shared, thread-safe building blocks called **concurrent data structures**.

As an unashamed dotnet evangelist, I spend a lot of my time working with the standard collections Microsoft ships in the Base Class Library (BCL). And the BCL's concurrent collections are genuinely excellent: FIFO queues (`ConcurrentQueue`), stacks (`ConcurrentStack`), unordered bags (`ConcurrentBag`), and of course the ubiquitous hash map (`ConcurrentDictionary`).

But what if you want a queue of prioritized items? Well, there's `PriorityQueue`, but it's not thread-safe, and there's no `ConcurrentPriorityQueue` (CPQ) in the BCL. In fact, almost no major runtime ships one as part of their standard library—the lone exception being Java's `PriorityBlockingQueue`. Unfortunately, it's literally a lock around a binary heap, which makes it functionally useless for most of the concurrent workloads you'd want it for.

This struck me as genuinely strange when I stumbled onto it some four years ago. Why aren't there CPQs *anywhere*?! Surely it couldn't be *that* hard to implement, right? Spoiler: It turns out that maintaining strict global order across dozens of threads without relying on a massive, performance-killing lock is an absolute nightmare. Who would've thought?

So began my foray into the wonderfully complicated world of concurrent programming! The result: the first truly scalable concurrent priority queue in dotnet.

---

## What a priority queue is (and where you've already used one)

A **priority queue** is a collection where every item carries a priority, and the only item you can pull out is the most important one currently inside. Arrival order doesn't matter: unlike a FIFO queue or a stack, a high-priority item added last still comes out first. Two operations carry the whole thing: **insert** an item with a priority, and **delete-min**, which pops the current best. (.NET's `PriorityQueue<TElement, TPriority>` is a min-queue, so dequeue returns the smallest priority.)

You've used one whether you noticed or not. Dijkstra's shortest path and A\* pathfinding are priority queues at heart, always expanding the nearest node next. So is every discrete-event simulation, every Huffman compressor, every OS scheduler, bandwidth shaper, and timer wheel. Any time code asks "what's the most important thing to do right now?", odds are a priority queue is answering.

## Why a *concurrent* one is so hard

Every other concurrent collection scales for one reason: the threads can stay out of each other's way. Two threads pushing a `ConcurrentQueue` touch opposite ends. Two threads writing different keys in a `ConcurrentDictionary` land in different buckets. Spread the work out, and more threads just means more throughput.

A priority queue can't spread anything out, and that's not an implementation problem. It's the definition. Every `delete-min` wants the single smallest element in the whole structure, so that one element becomes a spot every consumer is contractually required to fight over. Be as clever as you want underneath. If the contract says "give me the global minimum," then every deleting thread has to agree on what the minimum *is*, and getting threads to agree is the one thing that never scales.

There's the whole problem in a sentence: strict ordering hands you a single hot spot that every consumer has to pile onto. Lock the structure and the lock is your ceiling. Go lock-free and the hottest pointer is your ceiling. The bottleneck comes welded to the contract.

## Two data structures that should have worked

Abstract enough. Here are the two structures people actually build these out of.

The textbook priority queue, and the one under .NET's `PriorityQueue`, is a **binary heap** (a 4-ary heap, in .NET's case): a complete tree packed into a flat array. Sequentially it's a joy. Insert and delete-min are O(log n), peeking the min is O(1), and the whole thing lives in one cache-friendly array with no pointers to chase. Then you look at where the work actually lands. The minimum sits at the root, every `delete-min` tears out that root, and every `insert` can bubble a new item straight back up to it. The one node every operation touches is the same node, and the paths they walk overlap all the way down. Fine-grained locking turns into a deadlock-ordering puzzle (an insert climbing up runs headlong into a delete sinking down), so the only thing that reliably works is one big lock around the whole heap. Right back where we started: correct, and flat.

So people got clever and moved to a **skiplist**: a sorted linked list with randomized express lanes stacked on top, O(log n) search and nothing to rebalance. Skiplists come with elegant lock-free algorithms that have been studied to death, and they had the one property that looked like a way out. Inserts scatter. An item with a random priority splices in at a random spot, so writers hardly ever collide. For fifteen years the best concurrent priority queues we had were skiplist-based: Lotan–Shavit, Sundell–Tsigas, and the genuinely clever Lindén–Jonsson design that got delete-min down to roughly one compare-and-swap on the happy path.

And they do beat a locked heap. For a while. Then you keep adding threads and they go flat, usually somewhere between 8 and 32 cores. The inserts scatter, fine, but the minimum in a sorted list is always sitting at the *front*, and every `delete-min` still races for that same front. Lindén–Jonsson pushed the idea about as far as it goes: delete nodes logically, unlink them in bulk, keep threads off the head wherever you can. It helps. It can't win, because the front of a sorted list is just the root of the heap in a different outfit. The structure changed; the serial point didn't. (The strict designs that aren't skiplists, Mounds and CBPQ, run into the same wall.)

That's the thing the field took a decade to fully admit: the bottleneck was never the heap or the skiplist or the lock. It's strict ordering, full stop. As long as every consumer insists on the exact global minimum, they're all reaching for the same spot, and no data structure ever invented can spread out a fight that everybody wants to have in one place.

Which leaves one deeply uncomfortable question. What if `delete-min` didn't have to give back the real minimum?

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
