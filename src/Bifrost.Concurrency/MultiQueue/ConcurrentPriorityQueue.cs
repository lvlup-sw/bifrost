// =============================================================================
// <copyright file="ConcurrentPriorityQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry/Concurrency/MultiQueue/ConcurrentPriorityQueue.cs)

using System.Diagnostics;
using System.Numerics;
using Bifrost.Concurrency.MultiQueue;

namespace Bifrost.Concurrency;

/// <summary>
/// A thread-safe, unbounded-or-bounded priority queue built as a <i>MultiQueue</i> (DR-1): an
/// array of lock-striped sequential min-heaps (<see cref="SubQueue{TElement, TPriority}"/>) sized
/// to a power-of-two multiple of the processor count. Enqueues scatter across the sub-queues to
/// spread writer-lock contention; dequeues use a relaxed two-choice rule (sample two random
/// sub-queues, pop the better cached top) that trades a bounded rank error for near-linear
/// throughput scaling. This type is the public shell — the enqueue, dequeue, inspection, and
/// collection surfaces are added by later partial-class files.
/// </summary>
/// <typeparam name="TElement">The element type stored alongside each priority.</typeparam>
/// <typeparam name="TPriority">The priority type ordered by the queue's comparer.</typeparam>
/// <remarks>
/// <para>
/// <b>Sub-queue sizing.</b> The default sub-queue count is
/// <c>RoundUpToPowerOf2(4 × ProcessorCount)</c> — the multiplier 4 is the relaxed-concurrent-
/// priority-queue quality/throughput recommendation (Williams et&#160;al., ESA&#160;2021). A
/// power-of-two count lets the two-choice sampler pick a sub-queue with a single
/// <c>rng &amp; (count − 1)</c> mask instead of a modulo. The count is folded once into a
/// <c>static readonly</c> field so the JIT can treat it as a constant.
/// </para>
/// <para>
/// <b>Relaxation scales with core count.</b> Sizing the sub-queue array to the processor count is
/// what keeps the two-choice load-balance invariant constant across machines (at most <c>p</c> of
/// the <c>4p</c> sub-queues can be locked at once, so an operation finds a free sub-queue in O(1)
/// expected attempts on any host). The deliberate consequence is that the relaxed dequeue's
/// expected rank error — <c>(5/6)·n = (5/6)·4·ProcessorCount</c> — <i>grows with the core count</i>:
/// a pop is "looser" on a 64-core server (<c>n = 256</c>, error ≈ 213) than on an 8-core laptop
/// (<c>n = 32</c>, error ≈ 27). Relaxation and parallelism rise together by construction; this is a
/// feature of the MultiQueue, not a defect, but it means <see cref="TryDequeue"/>'s distance from
/// the true minimum is a property of the hardware, not a fixed constant (see <see cref="TryDequeue"/>).
/// </para>
/// <para>
/// <b>Comparer normalization (DR-6).</b> The queue stores the same DR-6-normalized comparer that
/// each sub-queue stores (<see cref="PriorityComparerHelpers.InitializeComparer{TPriority}"/>):
/// <see langword="null"/> for a value-type priority whose effective comparer is
/// <see cref="Comparer{T}.Default"/>, which selects the devirtualized default-comparer
/// path; any explicit comparer is retained as-is. The public <see cref="Comparer"/> property is
/// never null — it projects a stored null back to <see cref="Comparer{T}.Default"/>, matching the
/// <see cref="PriorityQueue{TElement, TPriority}"/> precedent.
/// </para>
/// <para>
/// <b>Bounded capacity (DR-13/DR-15).</b> Bounding is opt-in through the <c>boundedCapacity</c>
/// constructor; <see cref="BoundedCapacity"/> returns <c>-1</c> when the queue is unbounded (the
/// <see cref="System.Collections.Concurrent.BlockingCollection{T}.BoundedCapacity"/> precedent). A
/// single shared atomic gate (<c>_boundedCount</c>) enforces it: an enqueue reserves a slot before
/// touching any sub-queue and every successful removal releases one. The gate is touched only on
/// the bounded path (guarded by <c>_boundedCapacity &gt; 0</c>), so an unbounded queue executes no
/// <see cref="System.Threading.Interlocked"/> instruction against it.
/// </para>
/// <para>
/// <b>Thread Safety:</b> All public and protected members of
/// <see cref="ConcurrentPriorityQueue{TElement, TPriority}"/> are thread-safe and may be used
/// concurrently from multiple threads (the <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/>
/// precedent).
/// </para>
/// </remarks>
[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(ConcurrentPriorityQueueDebugView<,>))]
public sealed partial class ConcurrentPriorityQueue<TElement, TPriority>
{
    /// <summary>
    /// The default number of sub-queues: <c>RoundUpToPowerOf2(4 × ProcessorCount)</c>. Computed
    /// once into a <c>static readonly</c> field so it folds to a JIT constant; the multiplier 4 is
    /// the ESA 2021 relaxed-priority-queue quality/throughput recommendation, and rounding up to a
    /// power of two makes two-choice index selection a single <c>rng &amp; (count − 1)</c> mask.
    /// </summary>
    private static readonly int s_defaultSubQueueCount =
        (int)BitOperations.RoundUpToPowerOf2((uint)(4 * Environment.ProcessorCount));

    /// <summary>
    /// The lock-striped sub-queues. Their count is a power of two; index selection masks with
    /// <see cref="_subQueueMask"/>.
    /// </summary>
    private readonly SubQueue<TElement, TPriority>[] _queues;

    /// <summary>The two-choice index mask, equal to <c>_queues.Length − 1</c> (the count is a power of two).</summary>
    private readonly int _subQueueMask;

    /// <summary>
    /// The DR-6-normalized comparer shared with every sub-queue: <see langword="null"/> selects
    /// the devirtualized default-comparer path for value-type priorities; otherwise the explicit
    /// comparer. Retained at the queue level so queue-side ordering code can dual-path as well.
    /// </summary>
    private readonly IComparer<TPriority>? _comparer;

    /// <summary>The bounded capacity, or <c>-1</c> when the queue is unbounded.</summary>
    private readonly int _boundedCapacity;

    /// <summary>
    /// The default stickiness factor: <c>1</c> (resample every operation). Chosen so the default
    /// public contract is bit-identical to the pre-stickiness behavior — the
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>
    /// <c>concurrencyLevel</c> convention of a safe, unsurprising default behind an explicit opt-in.
    /// </summary>
    private const int DefaultStickiness = 1;

    /// <summary>
    /// The stickiness factor <c>s</c>: the number of consecutive operations a thread reuses a sampled
    /// sub-queue selection before re-sampling (ESA 2021). <c>1</c> leaves the relaxed-dequeue contract
    /// unchanged; larger values trade dequeue quality (expected rank error scales to <c>s·(5/6)·n</c>)
    /// for throughput on fixed-per-op-overhead-dominated regimes. Resolved once in the core constructor.
    /// </summary>
    private readonly int _stickiness;

    /// <summary>
    /// The shared atomic capacity gate (DR-13): the number of live reservations against
    /// <see cref="_boundedCapacity"/>. An enqueue increments it before selecting a sub-queue and
    /// rejects the element when the result exceeds the bound; every successful removal decrements
    /// it. The field is <i>only ever touched on the bounded path</i> (guarded by
    /// <c>_boundedCapacity &gt; 0</c>), so an unbounded queue executes no
    /// <see cref="System.Threading.Interlocked"/> instruction against it and it stays permanently
    /// zero. Accessed exclusively through interlocked / volatile operations, never a plain read or
    /// write.
    /// </summary>
    private int _boundedCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentPriorityQueue{TElement, TPriority}"/>
    /// class that is unbounded and orders priorities with <see cref="Comparer{T}.Default"/>.
    /// </summary>
    public ConcurrentPriorityQueue()
        : this(s_defaultSubQueueCount, boundedCapacity: -1, comparer: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentPriorityQueue{TElement, TPriority}"/>
    /// class that is unbounded and orders priorities with the specified comparer.
    /// </summary>
    /// <param name="comparer">
    /// The priority comparer, or <see langword="null"/> to use <see cref="Comparer{T}.Default"/>.
    /// </param>
    public ConcurrentPriorityQueue(IComparer<TPriority>? comparer)
        : this(s_defaultSubQueueCount, boundedCapacity: -1, comparer: comparer)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentPriorityQueue{TElement, TPriority}"/>
    /// class that is bounded to <paramref name="boundedCapacity"/> elements and orders priorities
    /// with the specified comparer.
    /// </summary>
    /// <param name="boundedCapacity">The maximum number of elements the queue may hold; must be positive.</param>
    /// <param name="comparer">
    /// The priority comparer, or <see langword="null"/> to use <see cref="Comparer{T}.Default"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="boundedCapacity"/> is less than or equal to zero.
    /// </exception>
    public ConcurrentPriorityQueue(int boundedCapacity, IComparer<TPriority>? comparer = null)
        : this(s_defaultSubQueueCount, boundedCapacity: ValidateBoundedCapacity(boundedCapacity), comparer: comparer)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentPriorityQueue{TElement, TPriority}"/>
    /// class with an explicit stickiness factor — a throughput/relaxation tuning hint modeled on the
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>
    /// <c>concurrencyLevel</c> constructor convention.
    /// </summary>
    /// <param name="boundedCapacity">
    /// The maximum number of elements the queue may hold (a positive value), or <c>-1</c> for an
    /// unbounded queue.
    /// </param>
    /// <param name="stickiness">
    /// The stickiness factor <c>s</c>: a thread reuses a sampled sub-queue selection for <c>s</c>
    /// consecutive operations before re-sampling, amortizing the sampling cost. Must be at least one;
    /// <c>-1</c> selects the default (<see cref="DefaultStickiness"/>). <b>Relaxation note:</b> the
    /// relaxed <see cref="TryDequeue"/>'s expected rank error scales to <c>s·(5/6)·n</c> — like the
    /// sub-queue count, the looseness a caller tolerates grows with this dial, so it compounds across
    /// hardware tiers. Keep it small; the paper's robust values are <c>{1, 4}</c>. <c>1</c> (the
    /// default of the other overloads) leaves the contract unchanged.
    /// </param>
    /// <param name="comparer">
    /// The priority comparer, or <see langword="null"/> to use <see cref="Comparer{T}.Default"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="boundedCapacity"/> is zero or less than <c>-1</c>, or
    /// <paramref name="stickiness"/> is less than one and not the <c>-1</c> default sentinel.
    /// </exception>
    public ConcurrentPriorityQueue(int boundedCapacity, int stickiness, IComparer<TPriority>? comparer = null)
        : this(s_defaultSubQueueCount, ValidateBoundedCapacityOrUnbounded(boundedCapacity), comparer, stickiness)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentPriorityQueue{TElement, TPriority}"/>
    /// class with an explicit sub-queue count. This is the core constructor that every public
    /// overload chains to; it is internal so tests and deterministic rank-error scenarios can pin
    /// the sub-queue count exactly (in particular <c>subQueueCount == 1</c> is honored as-is).
    /// </summary>
    /// <remarks>
    /// <b>Always call this constructor with named arguments.</b> A positional
    /// <c>(int, int, null)</c> argument list binds to the public
    /// <c>(boundedCapacity, stickiness, comparer)</c> overload instead — C# prefers the candidate
    /// that needs no default-argument substitution — silently reinterpreting the first two ints
    /// (e.g. <c>new ConcurrentPriorityQueue&lt;T, P&gt;(4, -1, null)</c> builds a
    /// bounded-to-4, default-sub-queue-count queue, not a 4-sub-queue unbounded one).
    /// </remarks>
    /// <param name="subQueueCount">
    /// The requested number of sub-queues; must be at least one. A value that is not already a
    /// power of two is rounded up to the next power of two so that two-choice index selection stays
    /// a single mask — values that are already powers of two (such as 1 and 4) are honored exactly.
    /// </param>
    /// <param name="boundedCapacity">The bounded capacity, or <c>-1</c> for an unbounded queue.</param>
    /// <param name="comparer">
    /// The priority comparer, or <see langword="null"/> to use <see cref="Comparer{T}.Default"/>.
    /// The value is DR-6-normalized for storage and passed unchanged to each sub-queue, which
    /// normalizes it the same way through the shared helper.
    /// </param>
    /// <param name="stickiness">
    /// The stickiness factor <c>s</c> (reuse a sampled selection for <c>s</c> consecutive ops); a
    /// trailing optional so the existing three-argument call sites keep compiling against the
    /// <see cref="DefaultStickiness"/> default. <c>-1</c> resolves to the default; values below one
    /// (and not <c>-1</c>) throw.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="subQueueCount"/> is less than one, or <paramref name="stickiness"/> is less
    /// than one and not the <c>-1</c> default sentinel.
    /// </exception>
    internal ConcurrentPriorityQueue(int subQueueCount, int boundedCapacity, IComparer<TPriority>? comparer, int stickiness = DefaultStickiness)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(subQueueCount, 1);
        _stickiness = ResolveStickiness(stickiness);

        // Round up to a power of two so two-choice index selection is `rng & (count - 1)`. Values
        // that are already powers of two (1, 4, the default 4×ProcessorCount round-up) are returned
        // unchanged, so the internal-count contract honors them exactly.
        int count = (int)BitOperations.RoundUpToPowerOf2((uint)subQueueCount);

        // DR-6 comparer normalization, shared with SubQueue so queue-level ordering code can also
        // dual-path later (see PriorityComparerHelpers.InitializeComparer).
        _comparer = PriorityComparerHelpers.InitializeComparer(comparer);

        _boundedCapacity = boundedCapacity;
        _subQueueMask = count - 1;
        _queues = new SubQueue<TElement, TPriority>[count];
        for (int i = 0; i < count; i++)
        {
            // Pass the ORIGINAL comparer: SubQueue performs the same DR-6 normalization itself.
            _queues[i] = new SubQueue<TElement, TPriority>(comparer);
        }
    }

    /// <summary>
    /// Gets the comparer used to order priorities. Never <see langword="null"/>: a stored null
    /// (the devirtualized default-comparer path) is projected back to
    /// <see cref="Comparer{T}.Default"/>, matching the <see cref="PriorityQueue{TElement, TPriority}"/>
    /// precedent.
    /// </summary>
    public IComparer<TPriority> Comparer => _comparer ?? Comparer<TPriority>.Default;

    /// <summary>
    /// Gets the bounded capacity of the queue, or <c>-1</c> when the queue is unbounded — the
    /// <see cref="System.Collections.Concurrent.BlockingCollection{T}.BoundedCapacity"/> precedent.
    /// </summary>
    public int BoundedCapacity => _boundedCapacity;

    /// <summary>Gets the number of sub-queues. Exposed for tests; equals <c>_queues.Length</c>.</summary>
    internal int SubQueueCountForTest => _queues.Length;

    /// <summary>Gets the resolved stickiness factor <c>s</c>. Exposed for tests.</summary>
    internal int StickinessForTest => _stickiness;

    /// <summary>
    /// Gets the backing sub-queue array. Exposed so later tasks can drive sub-queues directly
    /// (scan hooks, deterministic rank-error scenarios) without going through the public surface.
    /// </summary>
    internal SubQueue<TElement, TPriority>[] SubQueuesForTest => _queues;

    /// <summary>
    /// TEST-ONLY: a volatile snapshot of the shared atomic capacity gate
    /// (<see cref="_boundedCount"/>) — the number of live reservations against
    /// <see cref="_boundedCapacity"/>. On an unbounded queue this is permanently zero, because the
    /// unbounded path never increments the gate.
    /// </summary>
    internal int DebugBoundedCountForTest => Volatile.Read(ref _boundedCount);

    /// <summary>
    /// Releases one bounded-capacity reservation after a successful removal: when the queue is
    /// bounded, atomically decrements the shared gate (<see cref="_boundedCount"/>). This is the
    /// single release point that every successful-removal path funnels through —
    /// <c>TryPopFrom</c>'s success branch (covering both two-choice sampling and the verification
    /// scan) and <c>TryDequeueMin</c>'s success path — so the reservation accounting has exactly one
    /// decrement site to match the one increment site in <c>EnqueueCore</c>. On an unbounded queue
    /// (<c>_boundedCapacity &lt; 0</c>) it is a no-op that executes no
    /// <see cref="System.Threading.Interlocked"/> instruction.
    /// </summary>
    private void OnElementRemovedFromBounded()
    {
        if (_boundedCapacity > 0)
        {
            Interlocked.Decrement(ref _boundedCount);
        }
    }

    /// <summary>
    /// Validates a public <c>boundedCapacity</c> argument for the bounded constructor and returns
    /// it unchanged so it can be forwarded inline to the core constructor's chained call.
    /// </summary>
    /// <param name="boundedCapacity">The requested bounded capacity.</param>
    /// <returns>The validated <paramref name="boundedCapacity"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="boundedCapacity"/> is less than or equal to zero.
    /// </exception>
    private static int ValidateBoundedCapacity(int boundedCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boundedCapacity);
        return boundedCapacity;
    }

    /// <summary>
    /// Validates a <c>boundedCapacity</c> argument for the stickiness constructor, which—unlike
    /// <see cref="ValidateBoundedCapacity"/>—also accepts the unbounded sentinel <c>-1</c> so an
    /// unbounded queue can opt into stickiness. Returns the value unchanged for inline forwarding.
    /// </summary>
    /// <param name="boundedCapacity">The requested bounded capacity, or <c>-1</c> for unbounded.</param>
    /// <returns>The validated <paramref name="boundedCapacity"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="boundedCapacity"/> is zero or less than <c>-1</c>.
    /// </exception>
    private static int ValidateBoundedCapacityOrUnbounded(int boundedCapacity)
    {
        if (boundedCapacity != -1)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boundedCapacity);
        }

        return boundedCapacity;
    }

    /// <summary>
    /// Resolves and validates a stickiness argument: the <c>-1</c> sentinel maps to
    /// <see cref="DefaultStickiness"/>, any value at or above one passes through, and anything else
    /// throws — mirroring the <c>ConcurrentDictionary(concurrencyLevel)</c> validation convention
    /// (<c>concurrencyLevel &lt; 1</c> throws; <c>-1</c> means "use the default").
    /// </summary>
    /// <param name="stickiness">The requested stickiness, or <c>-1</c> for the default.</param>
    /// <returns>The resolved stickiness factor (at least one).</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="stickiness"/> is less than one and not the <c>-1</c> default sentinel.
    /// </exception>
    private static int ResolveStickiness(int stickiness)
    {
        if (stickiness == -1)
        {
            return DefaultStickiness;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(stickiness, 1);
        return stickiness;
    }
}
