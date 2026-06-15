// =============================================================================
// <copyright file="CpqBufferedAbBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Concurrency;

namespace Bifrost.Benchmarks.Concurrency;

/// <summary>
/// The buffered-vs-unbuffered A/B for the MultiQueue
/// <see cref="ConcurrentPriorityQueue{TElement, TPriority}"/>'s ESA 2021 §4 insertion/deletion
/// buffers (design <c>2026-06-15-cpq-buffered-multiqueue.md</c>, DR-8). A single
/// <see cref="BufferCapacity"/> axis toggles the buffer dial between <c>0</c> (off — bit-exact with
/// the pre-buffering queue) and <c>16</c> (the paper's optimum <c>C</c>), so every row of the BDN
/// table is a paired A/B point at the same population. The class is single-threaded by design — the
/// contended dense-throughput A/B is the separate fixed-window harness behind the
/// <c>buffered-throughput</c> verb (see <see cref="ThroughputVerbs.RunBufferedThroughputAb(string[])"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated class instead of a param on <see cref="CpqSingleThreadedLatencyBenchmarks"/>?</b>
/// That class fixes <c>bufferCapacity = 0</c> across a multi-target comparison (MultiQueue vs.
/// locking vs. raw); folding a buffer axis into it would multiply every unrelated baseline row by 2.
/// This class isolates the buffered MultiQueue against itself so the A/B table is exactly the
/// off-vs-on delta DR-8 gates on, with no incidental baseline rows.
/// </para>
/// <para>
/// <b>The <c>0 B/op</c> gate.</b> The <see cref="MemoryDiagnoserAttribute"/> <c>Allocated</c> column
/// must read <c>0 B</c> for the buffered (<see cref="BufferCapacity"/> = 16) steady-state
/// <c>Enqueue + Dequeue</c> pair — for <b>both</b> the value-type (<c>int</c>,<c>int</c>) and
/// reference-type (<c>string</c>,<c>long</c>) instantiations. The buffer moves go through
/// <c>Span.CopyTo</c>/gated <c>Span.Clear</c> over the <c>[InlineArray]</c> storage (DR-1, DR-4), so
/// the steady state reuses fixed inline storage and never touches the heap. The reference
/// instantiation is the load-bearing one: a <c>string</c> element forces the write-barrier-safe
/// slot-clear path that a value-type element elides, so a regression there (a boxed tuple, a stray
/// array) would surface in this column where the int path would hide it.
/// </para>
/// <para>
/// <b>The <see cref="Population"/> axis is exact for the steady-state pair shape only.</b> The
/// <c>Enqueue + Dequeue</c> pair holds the population flat (one in, one out per invocation), so its
/// rows are true per-population points: the latency-vs-heap-depth slope read down this axis is the
/// "does buffering flatten the deep-heap cache walk?" measurement DR-8 wants. The buffer's win is a
/// deep-heap effect (it removes the sift through a tall arity-4 heap from the hot path), so the
/// interesting rows are the large populations.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class CpqBufferedAbBenchmarks
{
    private ConcurrentPriorityQueue<int, int> _multiQueueInt = null!;
    private ConcurrentPriorityQueue<string, long> _multiQueueRef = null!;
    private string[] _refElements = null!;
    private int _counter;

    /// <summary>
    /// Gets or sets the buffer capacity <c>C</c> dialed into the queue: <c>0</c> = buffering off (the
    /// pre-buffering, bit-exact baseline), <c>16</c> = the ESA 2021 §4 optimum. This is the A/B axis —
    /// each population row is reported once per <see cref="BufferCapacity"/> value so the off-vs-on
    /// delta is read straight off two adjacent BDN rows.
    /// </summary>
    [Params(0, 16)]
    public int BufferCapacity { get; set; }

    /// <summary>
    /// Gets or sets the steady-state element count seeded into each queue before measurement. The
    /// sweep spans four orders of magnitude so the buffering effect is characterized from a shallow
    /// heap (10) up to a deep one (1M), where heap depth and cache behavior dominate and the buffer's
    /// "skip the deep-heap walk" win is expected to appear. The axis is exact only for the
    /// steady-state <c>EnqueueDequeue</c> pair shape (population held flat one-in/one-out).
    /// </summary>
    [Params(10, 1_000, 100_000, 1_000_000)]
    public int Population { get; set; }

    /// <summary>
    /// Seeds both queues with <see cref="Population"/> entries at the dialed <see cref="BufferCapacity"/>
    /// so the heaps (and, when buffering is on, the buffers) have reached a realistic steady state
    /// before the measured <c>Enqueue + Dequeue</c> pair runs.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        // Unbounded, default stickiness, dialed buffer capacity. The public four-argument ctor
        // (boundedCapacity, stickiness, bufferCapacity, comparer) is a distinct arity, so -1 sentinels
        // resolve to the unbounded / default-stickiness defaults without re-binding another overload.
        _multiQueueInt = new ConcurrentPriorityQueue<int, int>(boundedCapacity: -1, stickiness: -1, bufferCapacity: BufferCapacity);
        _multiQueueRef = new ConcurrentPriorityQueue<string, long>(boundedCapacity: -1, stickiness: -1, bufferCapacity: BufferCapacity);

        // Precompute the reference elements so the measured pair never allocates a string itself —
        // any non-zero Allocated then comes from the queue, which is exactly what the gate watches.
        _refElements = new string[Population];
        for (int i = 0; i < Population; i++)
        {
            _refElements[i] = $"e{i:D7}";
        }

        for (int i = 0; i < Population; i++)
        {
            _multiQueueInt.Enqueue(i, i);
            _multiQueueRef.Enqueue(_refElements[i], i);
        }

        _counter = 0;
    }

    /// <summary>
    /// Benchmarks an <c>Enqueue</c> immediately followed by a <c>TryDequeue</c> on the buffered
    /// MultiQueue with a value-type (<c>int</c>) element — the steady-state mixed workload the
    /// <c>0 B/op</c> gate targets, measured at the dialed <see cref="BufferCapacity"/>.
    /// </summary>
    /// <returns>The result of the <c>TryDequeue</c> (always consumed so the call is not elided).</returns>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("EnqueueDequeue", "Value")]
    public bool Buffered_EnqueueDequeue_ValueElement()
    {
        int v = _counter++;
        _multiQueueInt.Enqueue(v, v);
        return _multiQueueInt.TryDequeue(out _, out _);
    }

    /// <summary>
    /// Benchmarks an <c>Enqueue</c> immediately followed by a <c>TryDequeue</c> on the buffered
    /// MultiQueue with a reference-type (<c>string</c>) element and a value-type (<c>long</c>)
    /// priority — the write-barrier-safe slot-clear path that the value-type element elides. This is
    /// the load-bearing row for the <c>0 B/op</c> gate (DR-1, DR-4).
    /// </summary>
    /// <returns>The result of the <c>TryDequeue</c> (always consumed so the call is not elided).</returns>
    [Benchmark]
    [BenchmarkCategory("EnqueueDequeue", "Reference")]
    public bool Buffered_EnqueueDequeue_ReferenceElement()
    {
        int i = _counter++ % _refElements.Length;
        string element = _refElements[i];
        _multiQueueRef.Enqueue(element, i);
        return _multiQueueRef.TryDequeue(out _, out _);
    }
}
