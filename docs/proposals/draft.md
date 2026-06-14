## Background and motivation

`System.Collections.Concurrent` has thread-safe collections for FIFO (`ConcurrentQueue<T>`), LIFO (`ConcurrentStack<T>`), and unordered (`ConcurrentBag<T>`) access, but nothing priority-ordered. `PriorityQueue<TElement, TPriority>` was deliberately shipped as not thread-safe (#43957), so concurrent priority scenarios have no in-box answer.

No mainstream runtime ships a *scalable* concurrent priority queue today. Java's `PriorityBlockingQueue` is a global lock around a binary heap, and has been for twenty years. The lock-free strict designs in the literature (the Lotan–Shavit and Lindén–Jonsson skiplists, Mounds, CBPQ) saturate by roughly 8 to 32 threads. The reason is structural rather than incidental: a strict priority queue concentrates every consumer on the single global minimum, so that one element is a serial point by specification. Put the heap behind one lock and the lock is the ceiling; go lock-free and the head is still the ceiling.

The way past it is to relax the specification. Return an element with *a* small priority instead of *the* smallest, and state precisely how far off it can be. A decade of research explored that trade (SprayList, k-LSM, and others), and the **MultiQueue** [^spaa15] [^esa21] won it on simplicity and measured ordering quality, with later work showing it competitive with purpose-built concurrent schedulers [^ppopp22]. It now has exact theory behind it [^esa25].

### Demonstrated need

Community/ecosystem demand for this data structure spans over a decade:

| Issue   | Year | Request                                                      | Status      |
| ------- | ---- | ------------------------------------------------------------ | ----------- |
| #13903  | 2014 | Add priority queue (thread safety requested in comments)     | Closed      |
| #32700  | 2020 | IProducerConsumerCollection injection in Channels for priority support | Future      |
| #43957  | 2020 | PriorityQueue — explicitly rejected thread safety            | Implemented |
| #52205  | 2021 | Concurrent/multi-threaded version of PriorityQueue<T>        | Closed      |
| #62761  | 2021 | Priority Channels with async APIs for TTL-based ordering     | Future      |
| #101292 | 2024 | Channel.CreateBoundedPrioritized                             | Future      |

Third-party packages fill the gap imperfectly: the `ConcurrentPriorityQueue` NuGet package locks around every operation, and the popular `OptimizedPriorityQueue` is not thread-safe and carries a standing thread-safety request. Common use cases include priority channels / QoS pipelines (#32700, #101292), DelayQueue-style time-based scheduling (#62761), priority task schedulers, and A* pathfinding / game AI. The open Channels asks above would sit on this type once it exists.

### The acceptance criterion

@stephentoub stated the bar for new concurrent collections in [this comment](https://github.com/dotnet/runtime/issues/52205#issuecomment-833964252):

> Our policy is to only add a Concurrent collection when a) there is significant demonstrated need, and b) the implementation can be made faster and more scalable than just taking a lock around every operation.

The table above covers (a). This proposal targets (b) with a MultiQueue: an array of `n ≈ 4 × ProcessorCount` sub-queues, each an ordinary sequential arity-4 min-heap behind its own try-lock, each publishing its current minimum to a slot any thread can read without locking.

Enqueue try-locks one uniformly random sub-queue and inserts. The primary `TryDequeue` reads two random sub-queues' published minima without locking, then try-locks only the one with the smaller key and pops it. Nothing ever waits on a contended lock: a failed try-lock resamples a different sub-queue instead of blocking, and because at most `p` of the `4p` sub-queues can be held at once, a free one always exists. The expected number of lock acquisitions per operation is barely above one. Steady-state operations allocate nothing, no thread keeps private state, and there is no memory to reclaim, which suits a GC runtime.

The cost of all this is a relaxed primary dequeue, and the relaxation has a number. `TryDequeue` returns an element with one of the smallest priorities, not necessarily the global minimum, because only two sub-queue tops were ever candidates. The expected stationary rank error, meaning how many smaller keys it leaves behind, is exactly `(5/6)·n − 1 + 1/(6n)` for `n` sub-queues: a Markov-chain result, not an asymptotic estimate [^esa25]. Sampling two tops is what makes that bound finite at all. With single-choice deletion the rank error grows without bound [^podc17], the same power-of-choice effect that fixes randomized load balancing. When strict order is non-negotiable, `TryDequeueMin` scans every sub-queue top for the global minimum in O(n) and makes no scalability claim.

That structure scales where the strict designs cannot. Contention scatters across `n` independent sub-queues instead of piling onto one element, so throughput climbs with thread count rather than flattening. Measured on a 64-core Xeon (.NET 10), mixed 50/50 enqueue/dequeue throughput overtakes a global lock around a binary-heap priority queue from about four threads up and reaches 13–21× by 64 threads (workload dependent), climbing to roughly 114 M ops/s while the lock stays flat near 5–9 M ops/s. Single-thread pair latency is 1.4–2.2× a lock-wrapped `PriorityQueue` at zero steady-state allocation, so single-threaded code should keep using `PriorityQueue`, and the xmldoc says so. One caveat belongs up front: the relaxation scales with the machine, not the workload. Since `n ≈ 4 × ProcessorCount`, the same dequeue pops from roughly the top 27 on an 8-core box and the top 213 on a 64-core server. That is why `TryDequeueMin` is in the API rather than left as advice to wrap a lock.

The full algorithm, the rank-error theory, and animated diagrams are in [`BACKGROUND.md`](../../src/Bifrost.Concurrency/BACKGROUND.md). Per-workload scaling curves, single-thread latency, stickiness, and the rank-error validation are in the [benchmark report](../benchmarks/2026-06-13-cpq-xeon-8573c.md), collected with the split-workload methodology that exposed weaknesses in earlier relaxed designs [^gtw16]. Both links are in-repo; the repository is private, so public URLs are needed before filing.

[^spaa15]: Rihani, Sanders, Dementiev. *MultiQueues: Simple Relaxed Concurrent Priority Queues.* SPAA 2015 (brief announcement). [arXiv:1411.1209](https://arxiv.org/abs/1411.1209)
[^esa21]: Williams, Sanders, Dementiev. *Engineering MultiQueues: Fast Relaxed Concurrent Priority Queues.* ESA 2021, LIPIcs 204. [arXiv:2107.01350](https://arxiv.org/abs/2107.01350) (journal extension [arXiv:2504.11652](https://arxiv.org/abs/2504.11652))
[^esa25]: Walzer, Williams. *A Simple yet Exact Analysis of the MultiQueue.* ESA 2025, LIPIcs 351. [arXiv:2410.08714](https://arxiv.org/abs/2410.08714). Source of the exact stationary rank error `(5/6)·n − 1 + 1/(6n)`.
[^podc17]: Alistarh, Kopinsky, Li, Nadiradze. *The Power of Choice in Priority Scheduling.* PODC 2017. [arXiv:1706.04178](https://arxiv.org/abs/1706.04178). Single-choice deletion has unbounded rank error.
[^ppopp22]: Postnikova, Koval, Nadiradze, Alistarh. *Multi-Queues Can Be State-of-the-Art Priority Schedulers.* PPoPP 2022. [arXiv:2109.00657](https://arxiv.org/abs/2109.00657)
[^gtw16]: Gruber, Träff, Wimmer. *Benchmarking Concurrent Priority Queues.* [arXiv:1603.05047](https://arxiv.org/abs/1603.05047). Split-workload benchmarking methodology.
