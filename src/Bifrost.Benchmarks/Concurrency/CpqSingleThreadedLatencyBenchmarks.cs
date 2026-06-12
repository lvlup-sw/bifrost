// =============================================================================
// <copyright file="CpqSingleThreadedLatencyBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry.Benchmarks/SingleThreadedLatencyBenchmarks.cs);
// NaiveConcurrentPriorityQueue renamed to LockingPriorityQueue per design DR-1 ("Naive_*" methods now "Locking_*").

using BenchmarkDotNet.Attributes;

using Bifrost.Concurrency;

namespace Bifrost.Benchmarks.Concurrency;

/// <summary>
/// Single-threaded latency benchmarks. Measures per-operation latency and allocation (BDN
/// <see cref="MemoryDiagnoserAttribute"/> column) for the MultiQueue
/// <see cref="ConcurrentPriorityQueue{TElement, TPriority}"/> against two baselines: the
/// global-lock <see cref="LockingPriorityQueue{TElement, TPriority}"/> and a raw
/// <see cref="PriorityQueue{TElement, TPriority}"/> wrapped in a lock for apples-to-apples fairness.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a lock-wrapped raw queue?</b> The MultiQueue and locking queues are both thread-safe, so
/// every operation pays at least one lock acquisition. A bare <see cref="PriorityQueue{TElement, TPriority}"/>
/// would win simply by skipping synchronization the others cannot skip; wrapping it in the same kind
/// of uncontended <see cref="Lock"/> the others take keeps the comparison about the data structure,
/// not about whether locking happens at all. Single-threaded, the lock is always uncontended, so it
/// isolates the per-op overhead of the lock primitive itself as the constant the three share.
/// </para>
/// <para>
/// <b>Two operation shapes.</b> A pure <c>Enqueue</c> benchmark measures insertion cost in
/// isolation; an <c>Enqueue + Dequeue</c> pair measures the steady-state mixed workload that the
/// zero-allocation gate targets, where the population stays flat and storage is reused.
/// </para>
/// <para>
/// <b>String-priority variant.</b> Value-type priorities take the devirtualized default-comparer
/// path; a reference-type priority (<see cref="string"/>) cannot devirtualize the default and
/// instead dispatches through the cached comparer field. The <c>StringPriority</c> benchmark
/// exercises that second path for the MultiQueue so the comparer-dispatch cost is visible alongside
/// the int-priority numbers.
/// </para>
/// <para>
/// <b>The <see cref="Population"/> axis is exact only for the <c>EnqueueDequeue</c> pair shapes.</b>
/// The pair holds the population flat (one in, one out per invocation), so its rows are true
/// per-population points. The <c>Enqueue</c>-only shapes grow their queue with every BDN invocation
/// — by the end of a measured iteration a <c>Population = 10</c> queue holds millions of elements —
/// so their rows measure the amortized cost of insertion into a <i>growing</i> heap seeded at
/// <see cref="Population"/>, not insertion at a fixed size. (A faithful fixed-size enqueue-only
/// measurement is out of BDN's reach at this ~tens-of-ns scale: <c>[IterationSetup]</c> forces
/// single-invocation iterations, far below timer resolution.) Read the population sweep off the
/// pair benchmarks; treat the enqueue-only rows as population-agnostic relative comparisons.
/// </para>
/// <para>
/// This class is single-threaded by design — the multi-threaded throughput/scaling suite is the
/// separate fixed-window harness behind the <c>throughput</c> and <c>stickiness</c> verbs.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class CpqSingleThreadedLatencyBenchmarks
{
    private ConcurrentPriorityQueue<int, int> _multiQueueInt = null!;
    private ConcurrentPriorityQueue<int, int> _multiQueueStickyInt = null!;
    private LockingPriorityQueue<int, int> _lockingInt = null!;
    private PriorityQueue<int, int> _rawInt = null!;
    private PriorityQueue<int, int> _rawUnlockedInt = null!;
    private Lock _rawLock = null!;
    private ConcurrentPriorityQueue<string, string> _multiQueueString = null!;
    private string[] _stringKeys = null!;
    private int _counter;

    /// <summary>
    /// Gets or sets the steady-state element count seeded into each populated queue before
    /// measurement. The sweep spans four orders of magnitude so the
    /// MultiQueue-vs-bare-<see cref="PriorityQueue{TElement, TPriority}"/> overhead is characterized
    /// from a near-empty heap (10) up to a large one (1M), where heap depth and cache behavior
    /// dominate. The axis is exact only for the steady-state <c>EnqueueDequeue</c> pair shapes — the
    /// <c>Enqueue</c>-only shapes drift upward across BDN invocations (see the class remarks).
    /// </summary>
    [Params(10, 1_000, 100_000, 1_000_000)]
    public int Population { get; set; }

    /// <summary>
    /// Seeds each queue with <see cref="Population"/> entries so the heaps have grown past their
    /// initial capacity and the <c>Enqueue + Dequeue</c> benchmarks run against a realistic steady
    /// state rather than an empty structure.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _multiQueueInt = new ConcurrentPriorityQueue<int, int>();
        _multiQueueStickyInt = new ConcurrentPriorityQueue<int, int>(boundedCapacity: -1, stickiness: 4);
        _lockingInt = new LockingPriorityQueue<int, int>();
        _rawInt = new PriorityQueue<int, int>();
        _rawUnlockedInt = new PriorityQueue<int, int>();
        _rawLock = new Lock();
        _multiQueueString = new ConcurrentPriorityQueue<string, string>();

        // Precompute reference-type priority keys so the string benchmark measures the comparer path,
        // not per-iteration string formatting.
        _stringKeys = new string[Population];
        for (int i = 0; i < Population; i++)
        {
            _stringKeys[i] = $"k{i:D6}";
        }

        for (int i = 0; i < Population; i++)
        {
            _multiQueueInt.Enqueue(i, i);
            _multiQueueStickyInt.Enqueue(i, i);
            _lockingInt.Enqueue(i, i);
            _rawInt.Enqueue(i, i);
            _rawUnlockedInt.Enqueue(i, i);
            _multiQueueString.Enqueue(_stringKeys[i], _stringKeys[i]);
        }

        _counter = 0;
    }

    // ---- MultiQueue ConcurrentPriorityQueue<int, int> (the devirtualized value-type comparer path) ----

    /// <summary>Benchmarks a single <c>Enqueue</c> on the MultiQueue with an int priority.</summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Enqueue", "Int")]
    public void MultiQueue_Enqueue_Int()
    {
        int v = _counter++;
        _multiQueueInt.Enqueue(v, v);
    }

    /// <summary>Benchmarks an <c>Enqueue</c> immediately followed by a <c>TryDequeue</c> on the MultiQueue.</summary>
    /// <returns>The result of the <c>TryDequeue</c> (always consumed so the call is not elided).</returns>
    [Benchmark]
    [BenchmarkCategory("EnqueueDequeue", "Int")]
    public bool MultiQueue_EnqueueDequeue_Int()
    {
        int v = _counter++;
        _multiQueueInt.Enqueue(v, v);
        return _multiQueueInt.TryDequeue(out _, out _);
    }

    // ---- MultiQueue ConcurrentPriorityQueue<int, int> with stickiness s=4 (the throughput dial) ----

    /// <summary>
    /// Benchmarks a single <c>Enqueue</c> on the MultiQueue with stickiness <c>s = 4</c>, so the
    /// per-op cost of the sticky-index path is visible against the <c>s = 1</c> baseline.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Enqueue", "Int")]
    public void MultiQueueSticky4_Enqueue_Int()
    {
        int v = _counter++;
        _multiQueueStickyInt.Enqueue(v, v);
    }

    /// <summary>
    /// Benchmarks an <c>Enqueue</c> + <c>TryDequeue</c> pair on the MultiQueue with stickiness
    /// <c>s = 4</c> — the steady-state mixed cost stickiness is meant to lower on the weak regimes.
    /// </summary>
    /// <returns>The result of the <c>TryDequeue</c> (always consumed so the call is not elided).</returns>
    [Benchmark]
    [BenchmarkCategory("EnqueueDequeue", "Int")]
    public bool MultiQueueSticky4_EnqueueDequeue_Int()
    {
        int v = _counter++;
        _multiQueueStickyInt.Enqueue(v, v);
        return _multiQueueStickyInt.TryDequeue(out _, out _);
    }

    // ---- LockingPriorityQueue<int, int> baseline (global lock around PriorityQueue) ----

    /// <summary>Benchmarks a single <c>Enqueue</c> on the global-lock <see cref="LockingPriorityQueue{TElement, TPriority}"/> baseline.</summary>
    [Benchmark]
    [BenchmarkCategory("Enqueue", "Int")]
    public void Locking_Enqueue_Int()
    {
        int v = _counter++;
        _lockingInt.Enqueue(v, v);
    }

    /// <summary>Benchmarks an <c>Enqueue</c> + <c>TryDequeue</c> pair on the global-lock <see cref="LockingPriorityQueue{TElement, TPriority}"/> baseline.</summary>
    /// <returns>The result of the <c>TryDequeue</c> (always consumed so the call is not elided).</returns>
    [Benchmark]
    [BenchmarkCategory("EnqueueDequeue", "Int")]
    public bool Locking_EnqueueDequeue_Int()
    {
        int v = _counter++;
        _lockingInt.Enqueue(v, v);
        return _lockingInt.TryDequeue(out _, out _);
    }

    // ---- Raw PriorityQueue<int, int> wrapped in a lock (fairness baseline) ----

    /// <summary>
    /// Benchmarks a single <c>Enqueue</c> on a raw <see cref="PriorityQueue{TElement, TPriority}"/>
    /// under an uncontended lock, matching the synchronization the thread-safe queues cannot skip.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Enqueue", "Int")]
    public void RawLocked_Enqueue_Int()
    {
        int v = _counter++;
        lock (_rawLock)
        {
            _rawInt.Enqueue(v, v);
        }
    }

    /// <summary>
    /// Benchmarks an <c>Enqueue</c> + <c>TryDequeue</c> pair on a raw
    /// <see cref="PriorityQueue{TElement, TPriority}"/> under an uncontended lock.
    /// </summary>
    /// <returns>The result of the <c>TryDequeue</c> (always consumed so the call is not elided).</returns>
    [Benchmark]
    [BenchmarkCategory("EnqueueDequeue", "Int")]
    public bool RawLocked_EnqueueDequeue_Int()
    {
        int v = _counter++;
        lock (_rawLock)
        {
            _rawInt.Enqueue(v, v);
            return _rawInt.TryDequeue(out _, out _);
        }
    }

    // ---- Bare PriorityQueue<int, int>, NO lock (the true non-concurrent reference) ----

    /// <summary>
    /// Benchmarks a single <c>Enqueue</c> on a bare, unsynchronized
    /// <see cref="PriorityQueue{TElement, TPriority}"/>. Unlike the lock-wrapped baseline, this skips
    /// synchronization entirely — it is the honest "what do I give up by reaching for the concurrent
    /// type when I don't need concurrency?" reference a single-threaded caller actually compares against.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Enqueue", "Int")]
    public void RawUnlocked_Enqueue_Int()
    {
        int v = _counter++;
        _rawUnlockedInt.Enqueue(v, v);
    }

    /// <summary>
    /// Benchmarks an <c>Enqueue</c> + <c>TryDequeue</c> pair on a bare, unsynchronized
    /// <see cref="PriorityQueue{TElement, TPriority}"/> — the non-concurrent steady-state reference.
    /// </summary>
    /// <returns>The result of the <c>TryDequeue</c> (always consumed so the call is not elided).</returns>
    [Benchmark]
    [BenchmarkCategory("EnqueueDequeue", "Int")]
    public bool RawUnlocked_EnqueueDequeue_Int()
    {
        int v = _counter++;
        _rawUnlockedInt.Enqueue(v, v);
        return _rawUnlockedInt.TryDequeue(out _, out _);
    }

    // ---- MultiQueue ConcurrentPriorityQueue<string, string> (the reference-type comparer path) ----

    /// <summary>
    /// Benchmarks a single <c>Enqueue</c> on the MultiQueue with a string priority, exercising the
    /// cached-comparer dispatch path that reference-type priorities take.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Enqueue", "String")]
    public void MultiQueue_Enqueue_String()
    {
        string key = _stringKeys[_counter++ % _stringKeys.Length];
        _multiQueueString.Enqueue(key, key);
    }

    /// <summary>
    /// Benchmarks an <c>Enqueue</c> + <c>TryDequeue</c> pair on the MultiQueue with string priorities,
    /// exercising the reference-type comparer path on both the push and the pop.
    /// </summary>
    /// <returns>The result of the <c>TryDequeue</c> (always consumed so the call is not elided).</returns>
    [Benchmark]
    [BenchmarkCategory("EnqueueDequeue", "String")]
    public bool MultiQueue_EnqueueDequeue_String()
    {
        string key = _stringKeys[_counter++ % _stringKeys.Length];
        _multiQueueString.Enqueue(key, key);
        return _multiQueueString.TryDequeue(out _, out _);
    }
}
