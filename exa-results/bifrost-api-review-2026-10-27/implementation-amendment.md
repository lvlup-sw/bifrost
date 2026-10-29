> Historical implementation plan. See the [completed changes and measured validation](../../docs/benchmarks/2026-10-28-api-remediation.md).

# Preserve MultiQueue and simplify exact priority dispatch

This amendment supersedes the shared-heap extraction and public locking-queue expansion in the earlier implementation plan. The other remediation work remains applicable: accurate contracts, exception safety, isolated work-type configuration, precise admission results, lifecycle behavior, and the breaking API migration.

## Protect the existing algorithm

Treat the current MultiQueue implementation as the algorithmic baseline. Preserve random insertion routing; two-distinct-choice deletion and its tie rule; resampling on contention; stickiness periods and resets; default stripe sizing; occupancy fallback; and verification-scan behavior. Do not change distinct sampling to sampling with replacement merely to match a paper's formula. Correct the formula's stated applicability instead.

Preserve per-stripe locking and the existing cached-top publication protocol. Add no global lock, shared transaction journal, global version counter, or new shared atomic on the unbounded enqueue/dequeue path. Renaming best-observed inspection methods must not turn them into globally synchronized operations.

For buffering, preserve the partition of entries among the heap, insertion buffer, and deletion buffer. The deletion buffer must contain the resident minima, and its front must agree with the published top after a successful operation. Keep buffering and stickiness defaults unchanged. These are central features of the [MultiQueue design and buffering invariants](https://drops.dagstuhl.de/storage/00lipics/lipics-vol204-esa2021/LIPIcs.ESA.2021.81/LIPIcs.ESA.2021.81.pdf).

Implement comparer-exception safety inside the existing stripe implementation, without extracting a shared heap for a second production queue. Determine single-operation sift paths before mutation where possible; use a stripe-owned reusable journal only where buffered operations require rollback across multiple heap changes. Rollback must restore entries, heap and buffer counts, published state, and capacity accounting without further comparer calls. No heap-wide copy per operation and no steady-state allocation are acceptable.

Local changes can still lengthen critical sections and affect which stripes other threads select. Passing conservation tests alone is therefore insufficient evidence that the MultiQueue retains its behavior and performance.

## Recommendation on LockingPriorityQueue

Remove the public generic `LockingPriorityQueue<TElement, TPriority>` class in the breaking release, but retain exact priority dispatch as an orchestrator capability. This is a recommendation for the revised implementation, not a removal already performed.

The current public class is a thin synchronized wrapper around the BCL `PriorityQueue`. Its production consumer is the internal exact work-queue binding. Let that binding own a `PriorityQueue<WorkEnvelope<TWork>, long>` directly, protected by one lock covering heap access, count/admission decisions, and completion state. Keep the consumer wake-up and disposal protocols explicit; do not substitute an unsynchronized BCL queue.

The internal binding uses fixed `long` priorities and the default comparer, so it does not expose the arbitrary user-comparer contract that motivated MultiQueue rollback. This removes the reason to couple the MultiQueue heap to a second public implementation. Allocation failures and process-fatal failures are outside the comparer-exception guarantee.

Retain an independent lock-wrapped BCL queue in the benchmark project as a benchmark-only baseline. Move tests of exact dispatch, capacity, completion, and contention into the exact work-queue contract suite. Remove tests and package documentation for the removed public wrapper, and document migration for standalone users to a BCL priority queue with caller-owned synchronization.

Keeping the exact strategy is supported by the repository's [consumer-shaped soak](/Users/rsalus/Dev/aima/bifrost/docs/benchmarks/2026-06-cpq-soak.md): on its eight-worker configuration, recorded interactive p95 wait was approximately 1.0 second for locking versus 15.4 seconds for MultiQueue. These measurements support distinct workload choices, not a universal performance claim or starvation theorem. The separate [contended throughput measurements](/Users/rsalus/Dev/aima/bifrost/docs/benchmarks/2026-06-13-cpq-xeon-8573c.md) demonstrate why MultiQueue remains valuable at high contention.

## Required verification before integration

1. Record a baseline before algorithm-adjacent edits. Separate API/documentation changes from stripe exception-safety changes so their effects can be assessed independently.
2. Add test-only deterministic sampling controls. For successful, nonthrowing sequential histories with identical samples, compare element selection, stripe contents, buffer transitions, and publication behavior against the pre-change implementation. Use unique priorities for exact trace comparisons; validate equal-priority conservation separately.
3. Add failure injection at every comparer call position in insertion, deletion, eviction, flush, and refill. After each failure, verify the original entries remain and subsequent successful operations and clear/refill preserve capacity.
4. Run conservation, seqlock, occupancy, sparse-routing, stickiness, buffered invariants, and thread-churn tests. Keep existing empirical quality thresholds; do not loosen them to make the rewrite pass.
5. Compare rank-error distributions and per-element delay across repeated seeded workloads. Include sparse and dense populations, mixed producers/consumers, drain workloads, buffering on/off, and different stripe counts and stickiness settings.
6. Compare throughput, operation latency, allocations, and stripe lock-hold time on the same host. Keep the benchmark-only BCL baseline independent. Investigate repeatable regressions rather than attributing them automatically to stronger exception safety.
7. Run full-solution tests and Native AOT checks after migrating all consumers. Do not claim preservation of high-contention scaling from a low-core local smoke run; validate on suitable multicore hardware before release.

No production source was changed while evaluating this amendment.
