<!-- ============================================================================
FILING NOTES (delete this block before posting)

Where:    https://github.com/dotnet/runtime/issues/new?template=02_api_proposal.yml
Labels:   api-suggestion (auto-applied by the template); triage adds area-System.Collections
Process:  area owner sponsors -> api-ready-for-review -> FXDC review (Tuesdays, apireview.net/schedule)
Etiquette: keep the TOP issue body updated with a changelog as feedback lands; don't fork state into comments

BLOCKERS to resolve before filing:
1. PUBLIC PROTOTYPE LINK — lvlup-sw/bifrost is currently private. The issue below is written
   to be self-contained (all benchmark data inline), but the two prototype links marked
   [TODO-PUBLIC] need real URLs: either make the repo public, or publish
   LevelUp.Bifrost.Concurrency to NuGet and link the package + a public source mirror.
2. ARM64 — the pre-submission checklist's memory-ordering verification on ARM64 is still
   open. Either run the seqlock tear tests on an ARM64 box first, or soften the claim
   (currently worded "validated on x64; ARM64 stress runs planned").
3. The DR-8 600s soak is irrelevant here (orchestrator-level), no dependency.

The template has five fields; paste each section below into the matching field.
============================================================================ -->

# [API Proposal]: `ConcurrentPriorityQueue<TElement, TPriority>`

## Background and motivation

`System.Collections.Concurrent` has thread-safe collections for FIFO (`ConcurrentQueue<T>`), LIFO (`ConcurrentStack<T>`), and unordered (`ConcurrentBag<T>`) access, but no priority-ordered collection. `PriorityQueue<TElement, TPriority>` was deliberately designed as not thread-safe (#43957), which leaves concurrent scenarios unsupported.

No mainstream runtime ships a *scalable* concurrent priority queue today. Java's answer is still `PriorityBlockingQueue`, a global lock around a binary heap, essentially unchanged for twenty years; JCTools and Rust's crossbeam have none. This is a chance for the BCL to ship the first one with a quantified contract.

### Demonstrated need

The request spans more than a decade of issues and ecosystem workarounds:

| Issue | Year | Request | Status |
|-------|------|---------|--------|
| #13903 | 2014 | Add priority queue (thread safety requested in comments) | Closed |
| #32700 | 2020 | `IProducerConsumerCollection` injection in Channels for priority support | Future |
| #43957 | 2020 | `PriorityQueue` — explicitly rejected thread safety | Implemented |
| #52205 | 2021 | Concurrent/multi-threaded version of `PriorityQueue<T>` | Closed |
| #62761 | 2021 | Priority Channels with async APIs for TTL-based ordering | Future |
| #101292 | 2024 | `Channel.CreateBoundedPrioritized` | Future |

Third-party adoption fills the gap imperfectly: the `ConcurrentPriorityQueue` NuGet package (64K+ downloads) takes a lock around every operation; `OptimizedPriorityQueue` (~2M downloads) is not thread-safe and has a standing thread-safety request; Stack Overflow answers still point at ParallelExtensionsExtras sample code from 2010. Recurring use cases: priority channels / QoS pipelines (#32700, #101292), DelayQueue-style time-based scheduling (#62761), TTL cache eviction, priority task schedulers, A*/game AI. A BCL `ConcurrentPriorityQueue` is also the natural building block for the open Channels asks above.

### The acceptance criterion

@stephentoub stated the bar for new concurrent collections in https://github.com/dotnet/runtime/issues/52205#issuecomment-833964252:

> Our policy is to only add a Concurrent collection when a) there is significant demonstrated need, and b) the implementation can be made faster and more scalable than just taking a lock around every operation.

This proposal targets (b) with a **MultiQueue** (Rihani–Sanders–Dementiev, SPAA 2015 [3]; engineered in Williams–Sanders–Dementiev, ESA 2021 [1]): an array of `n ≈ 4 × ProcessorCount` sub-queues, each an ordinary sequential arity-4 min-heap behind its own lock. Enqueue try-locks one uniformly random sub-queue and inserts. The primary dequeue samples two random sub-queues' published minima *without locking*, then try-locks only the better one and pops. No operation ever waits on a contended lock; a failed try-lock resamples a different sub-queue, and since at most `p` of the `4p` sub-queues can be locked at once, a free one always exists (expected lock acquisitions per operation barely above one). Steady-state operations allocate zero bytes, the structure holds no thread-owned state, and it needs no memory-reclamation machinery — properties that fit a GC runtime unusually well.

The deliberate trade is a **relaxed primary dequeue with an exactly quantified contract**. Every strict design in the literature saturates by ~8–32 threads because all consumers fight over one global minimum. The MultiQueue's `TryDequeue` returns an element with *one of* the smallest priorities, and the relaxation has a number: the expected stationary rank error is `(5/6)·n − 1 + 1/(6n)` for `n` sub-queues — an exact Markov-chain result, not an asymptotic estimate (Walzer–Williams, ESA 2025 [2]). Crucially, single-choice sampling would have *unbounded* rank error (Alistarh et al., PODC 2017 [4]); the two-choice rule is what makes the bound exist. `TryDequeueMin` is the in-API strict escape hatch, an O(n) scan with no scalability claim.

### Performance data

Measured on a prototype implementation (~3.1K lines, .NET 10, details under "Prototype" below). Hardware: i9-13900K (24 physical / 32 logical), Linux x64, workstation GC. The contended numbers come from a fixed-window throughput harness (3 s windows, ×3 sweeps); single-threaded latency and allocations from BenchmarkDotNet with `MemoryDiagnoser`.

Mixed 50/50 enqueue/dequeue throughput, uniform random priorities, in M ops/s:

| Threads | MultiQueue | `lock(PriorityQueue<T,P>)` | Ratio |
|--------:|-----------:|---------------------------:|------:|
| 1 | 20.1 | 33.5 | 0.6× |
| 4 | 35.1 | 20.6 | 1.7× |
| 16 | 77.6 | 19.8 | 3.9× |
| 32 | 90.7 | 17.8 | 5.1× |
| 64 | 86.8 | 12.8 | 6.8× |

The 1-thread row is deliberately included: the MultiQueue carries ~46 ns of fixed per-op machinery (RNG draw, two lock-free top reads, try-lock, striped count) against ~32 ns for heap-under-lock, so the lock wins until contention appears, and wins longer on narrow key ranges where equal-key sifts terminate early (locking leads until 4 threads; the MultiQueue reaches 3.5× by 32). Users who never go concurrent should keep using `PriorityQueue<TElement, TPriority>`; the xmldoc says so.

Other gates, all green on the prototype:

- Single-threaded pair latency vs raw `PriorityQueue<T,P>`: 49.1 ns vs 37.5 ns = **1.31×** (budget was 2–3×); 0 B/op on both, including with string priorities.
- Steady-state allocations: **0 B/op** on every enqueue/dequeue path, `MemoryDiagnoser`-enforced in CI.
- Split producer/consumer workload (the configuration that collapsed k-LSM in published benchmarking [Gruber–Träff–Wimmer]): peak ~94–104 M ops/s at 64 threads, no collapse.
- Drain (prepopulated 1M, 16 threads): 51–55 M ops/s vs 3–6 for the lock.
- Rank-error distribution: measured mean 103.1 vs theory's 105.7 at p=64, c=2 (within 2.5%); seven CI regression tests assert mean/P99 bounds, two-choice monotonicity, and geometric tail decay against the published theory.
- A torn-read ("seqlock tear") stress suite with a kill-probe (the gate fails if the detection mechanism is disabled) validates the one bespoke concurrency mechanism. Validated on x64; ARM64 stress runs planned.

For hardware context, ESA 2021 Table 1 reports 126–600 M ops/s at p=64 on an EPYC 7702P for the C++ reference implementation; the managed prototype's curve shape matches.

## API Proposal

```csharp
namespace System.Collections.Concurrent;

/// <summary>
/// A thread-safe collection of elements ordered by priority. The primary TryDequeue is
/// relaxed: it removes an element with one of the smallest priorities (expected rank error
/// ≈ (5/6)·n for n ≈ 4 × ProcessorCount sub-queues). TryDequeueMin provides strict
/// best-effort minimum semantics at O(n) per call.
/// </summary>
public sealed class ConcurrentPriorityQueue<TElement, TPriority> :
    IEnumerable<(TElement Element, TPriority Priority)>,
    IReadOnlyCollection<(TElement Element, TPriority Priority)>
{
    public ConcurrentPriorityQueue();
    public ConcurrentPriorityQueue(IComparer<TPriority>? comparer);
    public ConcurrentPriorityQueue(int boundedCapacity, IComparer<TPriority>? comparer = null);

    // Snapshot semantics (the ConcurrentQueue<T>.Count precedent); prefer IsEmpty for emptiness.
    public int Count { get; }
    public bool IsEmpty { get; }
    // -1 when unbounded.
    public int BoundedCapacity { get; }
    // Never null (the PriorityQueue<TElement, TPriority> precedent).
    public IComparer<TPriority> Comparer { get; }

    // Throws InvalidOperationException only when the queue is bounded and full.
    public void Enqueue(TElement element, TPriority priority);
    public bool TryEnqueue(TElement element, TPriority priority);

    // RELAXED primary dequeue: removes an element with one of the smallest priorities.
    // False only when the queue was observed empty at some point during the call
    // (a full verification pass saw every sub-queue empty — the ConcurrentQueue precedent).
    public bool TryDequeue([MaybeNullWhen(false)] out TElement element,
                           [MaybeNullWhen(false)] out TPriority priority);

    // STRICT best-effort dequeue: O(n) scan of all sub-queue tops for the global minimum.
    // No scalability claim; the escape hatch when the relaxed rank error is unacceptable.
    public bool TryDequeueMin([MaybeNullWhen(false)] out TElement element,
                              [MaybeNullWhen(false)] out TPriority priority);

    // Best-effort minimum among the tops observed during the call; never blocks.
    public bool TryPeek([MaybeNullWhen(false)] out TElement element,
                        [MaybeNullWhen(false)] out TPriority priority);

    // Unordered, weakly consistent snapshot (the PriorityQueue.UnorderedItems +
    // ConcurrentDictionary precedents).
    public (TElement Element, TPriority Priority)[] ToArray();
    public Enumerator GetEnumerator();

    public struct Enumerator : IEnumerator<(TElement Element, TPriority Priority)>
    {
        public (TElement Element, TPriority Priority) Current { get; }
        public bool MoveNext();
        public void Reset();
        public void Dispose();
    }
}
```

Thread-safety and progress guarantees:

| Operation | Guarantee | Notes |
|---|---|---|
| `Enqueue` / `TryEnqueue` | wait-free locking | try-lock a random sub-queue; on contention resample, never wait |
| `TryDequeue` | wait-free locking | lock-free two-choice read of published tops; pop under one try-lock |
| `TryDequeueMin` / `TryPeek` | wait-free locking | lock-free O(n) top scan; at most one brief try-lock |
| `Count` / `IsEmpty` | lock-free | volatile sum / short-circuit over striped counts |
| `ToArray` / enumeration | weakly consistent | one sub-queue lock at a time, no global freeze, unordered |

"Wait-free locking" is the MultiQueue literature's progress property: no operation ever waits on a contended lock, and the resample loop has bounded expected steps by pigeonhole (at most `p` of `4p` sub-queues locked). It is an expected bound, not classical wait-freedom, and is documented as such.

## API Usage

```csharp
// Producer/consumer with priorities (lower dequeues first, PriorityQueue convention).
var queue = new ConcurrentPriorityQueue<WorkItem, int>();

queue.Enqueue(new WorkItem("critical"), priority: 1);
queue.Enqueue(new WorkItem("background"), priority: 100);

if (queue.TryDequeue(out var item, out var priority))
    Process(item);
```

```csharp
// The scalable path: many producers, many consumers. TryDequeue returns an element with
// ONE OF the smallest priorities — the documented trade that buys the scaling curve above.
var tasks = new ConcurrentPriorityQueue<WorkUnit, DateTime>();

Parallel.For(0, 1_000, i => tasks.Enqueue(new WorkUnit(i), DueTime(i)));

Parallel.For(0, Environment.ProcessorCount, _ =>
{
    while (tasks.TryDequeue(out var unit, out _))
        Execute(unit);
});
```

```csharp
// Strict minimum when ordering is non-negotiable: O(n), no scalability claim.
if (queue.TryDequeueMin(out var order, out var price))
    Settle(order);
```

```csharp
// Opt-in bound: enqueues are rejected, never blocked, when full.
var bounded = new ConcurrentPriorityQueue<Message, int>(boundedCapacity: 10_000);

if (!bounded.TryEnqueue(message, priority))
    await ApplyBackpressureAsync();
```

```csharp
// Custom comparer for max-heap behavior.
var maxFirst = new ConcurrentPriorityQueue<string, int>(
    Comparer<int>.Create(static (a, b) => b.CompareTo(a)));
```

## Alternative Designs

**A strict concurrent priority queue instead.** Strict minimum semantics put every consumer in contention for one element; every published strict design (Lotan–Shavit and Lindén–Jonsson skiplists, Mounds, CBPQ, SprayList's strict mode) saturates by ~8–32 threads, which fails criterion (b) — at that point a lock around `PriorityQueue` is simpler and roughly as fast. An earlier skiplist prototype of this same API lost to the global-lock baseline empirically before this design was selected. The relaxed-but-quantified contract also has BCL precedent on its side: `ConcurrentBag<T>.TryTake` returns "an item" with no ordering promise, `PriorityQueue` leaves equal-priority dequeue order unspecified, and `ConcurrentQueue.TryDequeue`'s false means "observed empty at some point during the call". This proposal just states its relaxation with a number.

**Naming: `RelaxedPriorityQueue`, or `TryDequeueAny` for the relaxed path.** Considered and rejected: the type is the concurrent priority queue users have been asking for by that name for a decade, and making the scalable path the plain `TryDequeue` (with the strict path discoverable as `TryDequeueMin`) steers users toward the default that actually scales. The relaxed contract is stated on the method's xmldoc rather than encoded in the type name. Open to FXDC guidance here.

**`TryRemove(priority)` / `ContainsPriority`.** Not possible at acceptable cost: a MultiQueue is an array of independent heaps with no global index, so priority lookup is O(total size) under every sub-queue lock. Callers needing search should pair the queue with a `ConcurrentDictionary`. (The earlier skiplist design had these; they are what made it slow.)

**Throwing `Dequeue`/`Peek`.** Omitted, following `ConcurrentQueue`/`ConcurrentStack`: emptiness is racy on a concurrent collection, so exception-on-empty is a trap. `Enqueue` throws only on bounded-full, mirroring bounded `BlockingCollection`.

**`ICollection` / `IProducerConsumerCollection<T>`.** `ICollection.SyncRoot` assumes a single freezing lock, an anti-pattern for a lock-striped structure. `IProducerConsumerCollection<T>` has a single type parameter (nowhere to put the priority) and FIFO/LIFO-flavored contracts; `PriorityQueue` omits it for the same reason. Bridging into Channels (#32700, #101292) is a natural follow-on once this type exists, but out of scope here.

**`Clear()`, `initialCapacity`, `EnqueueRange`.** `Clear()` would need a global freeze or expose ambiguous intermediate states; create a new instance. `initialCapacity` has no clean public meaning across a striped structure (per-sub-queue or total?), and pre-sizing showed no benefit in the prototype. `EnqueueRange` (which `PriorityQueue` has) is deferred: a concurrent batch insert is just a loop unless it promises atomicity, and atomic batch insert across stripes would require global locking; it can be added compatibly later if wanted.

**A tuning knob for sub-queue count or stickiness.** The literature parameterizes MultiQueues as MQ(c, s) (sub-queue multiplier, operation stickiness). The proposal fixes c = 4 (the ESA 2021 robust default) and s = 1 internally and exposes neither. The prototype validates that an opt-in stickiness hint (modeled on `ConcurrentDictionary`'s `concurrencyLevel` int) works and multiplies rank error by s exactly as theory predicts, so a knob could be added compatibly later; the first cut keeps the surface minimal.

## Risks

**Relaxed semantics may surprise users.** The primary `TryDequeue` does not return the global minimum. Mitigations: the quantified contract sits in the method's xmldoc rather than buried in remarks; `TryDequeueMin` is the in-API strict path; and the naming makes the scalable path the default while keeping the strict one discoverable.

**The relaxation scales with the machine, not the workload.** Expected rank error is `(5/6)·n` with `n ≈ 4 × ProcessorCount`: the same binary pops from roughly the top 27 on an 8-core box and the top 213 on a 64-core server. Code whose *correctness* (rather than throughput) depends on near-minimum dequeues will fail only on bigger hardware. This is documented prominently, and it is the reason `TryDequeueMin` exists in-API rather than as advice to "wrap a lock".

**Seqlock subtlety.** The one bespoke mechanism is a per-sub-queue seqlock publishing the cached top so the two-choice compare can read arbitrary `TPriority` values without locking. It is confined behind two methods (`PublishTop`/`TryReadTop`), gated by tear-detection stress tests with a kill-probe, and has a correctness-preserving try-lock-read fallback behind the same contract if the lock-free read path ever needs to be retired. Memory-model validation so far is x64; ARM64 stress runs are planned before any implementation PR.

**Reference-type `TPriority` dispatch.** Shared-generic dispatch cannot devirtualize `Comparer<T>.Default` for reference types (#10050). Documented; the seqlock read path avoids comparer calls entirely.

**Single-threaded overhead.** ~1.3× a raw `PriorityQueue` and slower than heap-under-lock until ~4 threads (measured above). The docs steer single-threaded users to `PriorityQueue<TElement, TPriority>`.

**Enumeration is unordered and weakly consistent.** Matches `PriorityQueue.UnorderedItems` and `ConcurrentDictionary` precedent, but differs from what some users may expect; callers sort the snapshot if they need order.

---

### Prototype

A production implementation of exactly this design ships in [`LevelUp.Bifrost.Concurrency`]([TODO-PUBLIC: NuGet/package link]) ([source]([TODO-PUBLIC: repo link])): ~3.1K lines, .NET 10, AOT/trim-safe, with the full verification battery (element-conservation stress, seqlock tear tests with kill-probe, rank-error distribution gates asserting the ESA 2025 theory, thread-churn tests) and the benchmark harness that produced the tables above. An annotated theoretical background with animated algorithm diagrams is in the repo ([BACKGROUND.md]([TODO-PUBLIC: link])). The original research prototype (lvlup-sw/DataFerry) is frozen and retained for provenance.

### References

1. Williams, Sanders, Dementiev. *Engineering MultiQueues: Fast Relaxed Concurrent Priority Queues.* ESA 2021, LIPIcs 204. arXiv:2107.01350 (journal extension arXiv:2504.11652)
2. Walzer, Williams. *A Simple yet Exact Analysis of the MultiQueue.* ESA 2025, LIPIcs 351. arXiv:2410.08714 — exact stationary rank error `(5/6)·n − 1 + 1/(6n)`
3. Rihani, Sanders, Dementiev. *MultiQueues: Simple Relaxed Concurrent Priority Queues.* SPAA 2015 (brief announcement). arXiv:1411.1209
4. Alistarh, Kopinsky, Li, Nadiradze. *The Power of Choice in Priority Scheduling.* PODC 2017. arXiv:1706.04178 — single-choice deletion has unbounded rank error
5. Postnikova, Koval, Nadiradze, Alistarh. *Multi-Queues Can Be State-of-the-Art Priority Schedulers.* PPoPP 2022. arXiv:2109.00657
6. Gruber, Träff, Wimmer. *Benchmarking Concurrent Priority Queues.* arXiv:1603.05047 — the split-workload methodology used in the gates above
