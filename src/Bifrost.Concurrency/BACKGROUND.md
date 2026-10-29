# Background: the MultiQueue inside Bifrost.Concurrency

The [README's priority-dispatch overview](../../README.md#priority-dispatch) describes the available bindings. This file explains the design behind them. What the data structure actually is, what it trades away, the bounds that govern the trade, and how the orchestrator builds class-aware scheduling on top of it. The figures are animated SVGs and play inline on GitHub; each loops every 10–14 seconds.

Everything here is distilled from the research corpus this port descends from (the DataFerry repo, frozen at `2bf0456`: `docs/research/2026-06-09-cpq-v2-theoretical-basis.md`, the v2 and stickiness design docs, and the BCL API proposal) plus the papers in [References](#references).

## Why a concurrent priority queue is hard

A priority queue concentrates all contention on a single element, because every consumer wants the same thing: the minimum. Put one heap behind one lock and that lock is your ceiling. Go lock-free and the head is still your ceiling, just with more exotic failure modes. Every published strict design, from the Lotan–Shavit skiplist through Mounds and CBPQ, saturates at or below roughly 32 threads, and not for lack of engineering effort. The head is a serial point *by specification*.

What actually moves the ceiling is changing the specification: return *a* small key instead of *the* smallest key, and state precisely how wrong you are allowed to be. Relaxed designs spent a decade exploring that trade (SprayList, k-LSM, ZMSQ, Multi Bucket Queues). The MultiQueue won it on simplicity and measured ordering quality, and it has unusually solid theory behind it for something this easy to describe (Rihani–Sanders–Dementiev, SPAA 2015; the ESA 2021 engineering paper; an exact analysis at ESA 2025).

Worth knowing for context: no mainstream runtime ships a scalable concurrent priority queue. Java's `PriorityBlockingQueue` is a global lock around a binary heap and has been for twenty years. .NET's `PriorityQueue` was designed non-thread-safe on purpose (dotnet/runtime#43957), and requests for a concurrent one go back to 2014. DataFerry built this implementation as a BCL API proposal; Bifrost is where it ships.

## The algorithm, in one figure

Split the queue into **n = 4 × ProcessorCount** sub-queues (rounded up to a power of two, so picking one is a bit-mask, not a division). Each sub-queue is a perfectly ordinary *exact* heap guarded by a try-lock, and it publishes its current minimum to a slot that anyone may read without locking.

<p align="center"><img src="diagrams/anim-multiqueue-basic.svg" width="880" alt="Animated walkthrough: enqueue locks one random sub-queue and inserts; deleteMin reads two random published tops without locking, locks only the winner, and pops it. Sometimes the true global minimum simply is not examined."/></p>

Both operations follow the same discipline:

- enqueue picks one sub-queue uniformly at random, try-locks it, inserts, unlocks. If the try-lock fails it does not wait; it picks a different sub-queue and tries again.
- deleteMin reads the published tops of *two* random sub-queues, taking no locks at all, then try-locks whichever showed the smaller key, re-checks under the lock, and pops. Failed try-lock, same rule: resample.

Try-lock resampling reduces contention when enough stripes are available. The research progress argument assumes a bound on participating threads; ProcessorCount does not enforce that bound in this library. Enqueue retries and dequeue verification can continue indefinitely under contention, especially when the sole populated stripe is locked. This implementation is neither lock-free nor wait-free.

## Rank error: the price, with a number on it

The contract is strange the first time you meet it. `TryDequeue` returns an element with *one of* the smallest priorities, and it is allowed to miss the actual minimum, because only two sub-queue tops were ever candidates. The miss is measured as **rank error**: how many smaller keys got left behind.

<p align="center"><img src="diagrams/anim-rank-error.svg" width="880" alt="Animated illustration: three pops leave 0, 2, and 1 smaller keys behind; the expected stationary rank error is (5/6)·n − 1 + 1/(6n)."/></p>

The ESA 2025 stationary rank formula **(5/6)·n − 1 + 1/(6n)** concerns an idealized deletion-only model with independent sampling with replacement. The implementation samples two distinct stripes and adds stickiness, buffers, occupancy fallback and verification. The formula and illustration above are research context, not an exact contract for these operations. The paper analyzes a limiting rank distribution; the suite's mean and P99 thresholds are empirical engineering gates, not deterministic limits.

Correctness must not depend on distance from the global minimum. More stripes and greater stickiness can change quality as well as throughput. Measure the intended workload. The public `LockingPriorityQueue` has been removed; exact orchestrator dispatch remains an internal synchronized BCL heap. Standalone exact ordering requires caller-owned synchronization around a BCL priority queue. See the [API migration](../../docs/migrations/2026-10-priority-api.md).

## What the .NET implementation does differently

The port is the DataFerry v2 design, about 3.1K lines under `MultiQueue/`. Where it departs from the C++ reference implementations, the reason is always the managed runtime:

- The seqlock-published top has no counterpart in the C++ reference implementations, which require atomically readable keys and so restrict priorities to word-sized types. Writers (always under the sub-queue lock) bump a version counter to odd, write the priority plus an emptiness flag, and bump back to even. Readers spin on even-and-unchanged with a handful of retries, then give up and resample another queue. `Volatile.Write` release on the writer side and `Volatile.Read` acquire on the reader side make the even-version check sufficient to exclude torn reads. If you only read one part of the source, read this one (`SubQueueHeader.cs`).
- Heaps are arity-4 with hole-based sift, one final write instead of swap pairs, rather than the paper's arity-8. On .NET every reference move pays a GC write barrier, so fewer levels times fewer writes wins.
- Every sub-queue's hot header (lock word, published top, stripe count) is padded to 128-byte boundaries with explicit-layout structs, the only reliable false-sharing tool when a compacting GC can relocate objects. 128 covers both Apple/ARM line sizes and Intel's adjacent-line prefetch.
- Per-thread state is a `[ThreadStatic]` handle carrying an inline xoshiro256\*\* generator, modeled on the thread pool's own internals. Thread identity is only ever a striping hint. Stale state is harmless, so the structure survives thread-pool churn and work migrating across `await`.
- Stickiness (the MQ(c, s) knob that reuses sampled sub-queues for s consecutive operations, trading ordering quality for cache locality) ships defaulted to s = 1, which preserves the default sampling behavior.
- Emptiness is reported honestly. A failed sampled pop only proves two sub-queues were empty, so `TryDequeue` returns false only after a full verification scan over all n sub-queues, the same "observed empty at some point during the call" wording `ConcurrentQueue` uses. `Count` sums striped counters with snapshot semantics.

## How Bifrost turns the queue into a scheduler

The CPQ orders whatever keys it is given. The orchestrator's contribution is policy: what a key means, and what happens at the door when the queue is nearly full.

### The virtual-time key

`key = enqueueTicks − classBoost`. Interactive work gets boosted by the window (30 s by default); Batch gets nothing.

<p align="center"><img src="diagrams/anim-virtual-time-boost.svg" width="880" alt="Animated illustration: an Interactive arrival at t=42 takes key 12 and passes a fresh Batch item, but cannot pass one that has already waited longer than the 30-second window."/></p>

With the default class offsets, an item that has waited longer than the boost window has a smaller key than a fresh arrival. The window bounds the advantage in the key, not the time an item can wait: worker availability and the number of older items also matter. The locking binding selects the minimum key; MultiQueue can select a different key because its ordering is relaxed.

### Watermark admission

Ordering decides who goes first. Watermarks decide who gets in when the bounded queue fills, and they shed the cheapest class first.

<p align="center"><img src="diagrams/anim-watermark-admission.svg" width="880" alt="Animated illustration: Batch is rejected above 0.90 of capacity, Default above 0.95, Interactive admits to the brim, and every rejection returns immediately as an EnqueueResult."/></p>

Admission is fail-fast on purpose. Waiting for space at capacity would hand the priority inversion right back, because a queued Batch backlog would then block an Interactive *producer* at the door. That reasoning is the root of the v0.5.0 breaking change: `EnqueueAsync` returns `ValueTask<EnqueueResult>`, a rejection is a value rather than an exception, and eligible admission rejections are dead-lettered with `AttemptCount = 0` when a dead-letter queue is configured.

## What the benchmarks say

Three committed result sets back the claims above. The charts regenerate from the result docs with `python3 src/Bifrost.Concurrency/diagrams/generate_charts.py`.

Contended throughput first, the regime the MultiQueue exists for. The relaxed structure climbs to core saturation while the exact lock collapses, and the port reproduces DataFerry's published curves on the same physical machine (BDN pair rows within ±4%, 0 B/op preserved). Details in the [port-parity doc](../../docs/benchmarks/2026-06-cpq-port-parity.md).

<p align="center"><img src="diagrams/chart-throughput-contended.svg" width="720" alt="MultiQueue throughput climbs from 15 to about 82 M ops/s with thread count while the locking baseline falls from 22 to about 13."/></p>

Then the [consumer-shaped soak](../../docs/benchmarks/2026-06-cpq-soak.md), which is the regime Bifrost actually runs in: a handful of workers, seconds-long work items, a queue that is barely contended. Here the trade inverts, and it inverts for exactly the reason §3 predicts. With capacity 128 and n ≈ 128 sub-queues on a 32-core host, the expected rank error is the same order as the entire population, so the MultiQueue's class ordering washes out, while a global lock touched once per multi-second work item never convoys. These are the 600 s release-soak numbers, and the gap is not subtle:

<p align="center"><img src="diagrams/chart-soak-interactive-p95.svg" width="720" alt="Interactive p95 queue wait: locking binding 33.4 s versus MultiQueue 93.3 s at 2 workers; 1.0 s versus 15.4 s at 8 workers."/></p>

This is why both bindings ship and why the README's guidance defaults the priority strategy to `LockingPriorityWorkQueue`. Each binding's home regime is guarded by its own benchmark, so neither can quietly regress.

Finally the [DR-7 FIFO gate](../../docs/benchmarks/2026-06-cpq-orchestrator-baseline.md), which enforced that users who never opt into priority dispatch pay nothing for the port. The first `IWorkQueue` rewrite failed it, roughly 2× latency from async-wrapper boxing. The remediation (forwarding the channel's pooled `ValueTask` directly, plus a try-write-first enqueue) restored the deterministic 0 B/op single-worker profile; what remains is the priced-in cost of the envelope feature itself.

<p align="center"><img src="diagrams/chart-dr7-fifo-gate.svg" width="720" alt="Round-trip times: baseline 2.1/3.1/10.3 ms, post-rewrite 4.5/6.0/19.6 ms (gate FAIL), post-fix 3.0/3.1/12.3 ms (gate PASS)."/></p>

## Which queue, when

| Regime | Use | Why |
|---|---|---|
| Many workers, micro work items, hot queue | `ConcurrentPriorityQueue` | Contention scatters across n sub-queues; the lock convoys (1.7–17.5× from 4 threads up) |
| 1–2 threads | the exact lock | Fixed per-op cost: ~46 ns of RNG + two seqlock peeks + try-lock, versus ~32 ns for heap-under-lock |
| Few workers, seconds-long items (Bifrost's default regime) | `LockingPriorityWorkQueue` | The lock is touched once per multi-second item and cannot convoy; exact ordering gives truthful class separation |
| Narrow key ranges at low thread counts | the exact lock | Equal-key sifts terminate early; the baseline's critical section drops to ~26 ns |
| Exact-min semantics required (the #16 scheduler) | BCL `PriorityQueue` with caller-owned synchronization | A relaxed pop is contractually allowed to miss the minimum |

## References

1. Rihani, Sanders, Dementiev. *Brief Announcement: MultiQueues: Simple Relaxed Concurrent Priority Queues.* SPAA 2015. arXiv:1411.1209
2. Williams, Sanders, Dementiev. *Engineering MultiQueues: Fast Relaxed Concurrent Priority Queues.* ESA 2021, LIPIcs 204. arXiv:2107.01350 (journal extension arXiv:2504.11652)
3. Walzer, Williams. *A Simple yet Exact Analysis of the MultiQueue.* ESA 2025, LIPIcs 351. arXiv:2410.08714. Source of the exact stationary rank error
4. Alistarh, Kopinsky, Li, Nadiradze. *The Power of Choice in Priority Scheduling.* PODC 2017. arXiv:1706.04178. Single-choice unboundedness; the O(p) two-choice bound
5. Postnikova, Koval, Nadiradze, Alistarh. *Multi-Queues Can Be State-of-the-Art Priority Schedulers.* PPoPP 2022. arXiv:2109.00657
6. Alistarh, Kopinsky, Li, Shavit. *The SprayList: A Scalable Relaxed Priority Queue.* PPoPP 2015
7. Wimmer, Gruber, Träff, Tsigas. *The Lock-free k-LSM Relaxed Priority Queue.* PPoPP 2015. arXiv:1503.05698
8. Gruber, Träff, Wimmer. *Benchmarking Concurrent Priority Queues.* arXiv:1603.05047. The split-workload cautionary tale behind the project's benchmark hygiene rules
9. von Geijer, Tsigas. *How to Relax Instantly: Elastic Relaxation of Concurrent Data Structures.* Euro-Par 2024 (Best Paper); IEEE TPDS 36(12), 2025

Strict-design context, for the saturation claim in §1: Lotan–Shavit (IPDPS 2000), Lindén–Jonsson (OPODIS 2013), Mounds (ICPP 2012), CBPQ (Euro-Par 2016), Sagonas–Winblad (LCPC 2016), TSLQueue (Euro-Par 2021), PIPQ (DISC 2025), ZMSQ (ICPP 2019), Multi Bucket Queues (SPAA 2024).

The animated-figure approach is borrowed from Kåre von Geijer's [MultiQueue introduction](https://karevongeijer.com/blog/multiqueue-introduction/), which is also simply a good read.
