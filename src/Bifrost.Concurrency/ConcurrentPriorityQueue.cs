// =============================================================================
// <copyright file="ConcurrentPriorityQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Bifrost.Concurrency;

/// <summary>
/// A thread-safe priority queue (bounded or unbounded) built as a <i>MultiQueue</i>: an
/// array of lock-striped min-heaps (<see cref="SubQueue{TElement, TPriority}"/>) sized to a
/// power-of-two multiple of the processor count. Enqueues scatter across sub-queues to spread
/// lock contention; dequeues sample two random sub-queues and pop the better cached top, trading
/// bounded rank error for near-linear scaling. Later partial-class files add the enqueue, dequeue,
/// inspection, and collection surfaces.
/// </summary>
/// <typeparam name="TElement">The element type stored alongside each priority.</typeparam>
/// <typeparam name="TPriority">The priority type ordered by the queue's comparer.</typeparam>
/// <remarks>
/// <para>
/// The default sub-queue count is <c>RoundUpToPowerOf2(4 × ProcessorCount)</c>. The multiplier 4
/// is the relaxed-priority-queue quality/throughput recommendation from Williams et&#160;al.
/// (ESA&#160;2021), and a power-of-two count lets the two-choice sampler pick a sub-queue with a
/// single <c>rng &amp; (count − 1)</c> mask instead of a modulo. The count folds into a
/// <c>static readonly</c> field so the JIT treats it as a constant.
/// </para>
/// <para>
/// Sizing the sub-queue array to the processor count keeps the two-choice load-balance invariant
/// constant across machines: at most <c>p</c> of the <c>4p</c> sub-queues can be locked at once, so
/// an operation finds a free sub-queue in O(1) expected attempts on any host. The consequence is
/// that the relaxed dequeue's expected rank error, <c>(5/6)·n = (5/6)·4·ProcessorCount</c>, grows
/// with the core count: a pop is looser on a 64-core server (<c>n = 256</c>, error ≈ 213) than on
/// an 8-core laptop (<c>n = 32</c>, error ≈ 27). Relaxation and parallelism rise together, so
/// <see cref="TryDequeue"/>'s distance from the true minimum is a property of the hardware, not a
/// fixed constant.
/// </para>
/// <para>
/// The queue stores the same normalized comparer that each sub-queue stores
/// (<see cref="PriorityComparerHelpers.InitializeComparer{TPriority}"/>): <see langword="null"/>
/// for a value-type priority whose effective comparer is <see cref="Comparer{T}.Default"/>, which
/// selects the devirtualized default-comparer path; any explicit comparer is kept as-is. The public
/// <see cref="Comparer"/> property is never null. It projects a stored null back to
/// <see cref="Comparer{T}.Default"/>, matching the <see cref="PriorityQueue{TElement, TPriority}"/>
/// precedent.
/// </para>
/// <para>
/// Bounding is opt-in through the <c>boundedCapacity</c> constructor; <see cref="BoundedCapacity"/>
/// returns <c>-1</c> when the queue is unbounded (the
/// <see cref="System.Collections.Concurrent.BlockingCollection{T}.BoundedCapacity"/> precedent). A
/// single shared atomic gate (<c>_boundedCount</c>) enforces it: an enqueue reserves a slot before
/// touching any sub-queue, and every successful removal releases one. The gate is touched only on
/// the bounded path (guarded by <c>_boundedCapacity &gt; 0</c>), so an unbounded queue runs no
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
    /// The per-instance occupancy bitmask (DR-1): one bit per sub-queue, length
    /// <c>(n + 63) &gt;&gt; 6</c> words. Bit <c>i</c> (word <c>i &gt;&gt; 6</c>, position <c>i &amp; 63</c>) is
    /// set exactly when sub-queue <c>i</c> has published itself non-empty, and cleared when it
    /// publishes empty. The array is allocated once in the core constructor and shared <i>by
    /// reference</i> with every <see cref="SubQueue{TElement, TPriority}"/>, which is told its own
    /// index; each sub-queue writes its bit under its own <c>SyncLock</c> on a boundary crossing
    /// using <see cref="Interlocked.Or(ref ulong, ulong)"/> / <see cref="Interlocked.And(ref ulong, ulong)"/>
    /// (atomic per-word, so two sub-queues sharing a word never lose an update; DR-2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a lock-free-<i>read</i> hint consulted only by the relaxed dequeue's sparse-fallback
    /// routing phase (DR-3): after the two-choice sampling budget is spent, the consumer reads the
    /// occupancy words and routes straight to a populated sub-queue via
    /// <see cref="BitOperations.TrailingZeroCount(ulong)"/> instead of falling to the O(n)
    /// verification scan. The bitmask is purely a hint — the verification scan stays the sole
    /// authority for returning <see langword="false"/> — so a stale read can only cost a wasted
    /// routing attempt, never a wrong answer (DR-4).
    /// </para>
    /// <para>
    /// On the dense hot path the bitmask is never written (sub-queues never reach size zero under
    /// load) and never read (sampling lands a pop within budget), so it sits dormant in cache and
    /// adds no per-operation allocation (DR-5). Unused high bits in the final word of a
    /// non-multiple-of-64 sub-queue count stay zero and are never interpreted as occupied.
    /// </para>
    /// </remarks>
    private readonly ulong[] _occupancy;

    /// <summary>
    /// The normalized comparer shared with every sub-queue: <see langword="null"/> selects
    /// the devirtualized default-comparer path for value-type priorities; otherwise the explicit
    /// comparer. Retained at the queue level so queue-side ordering code can dual-path as well.
    /// </summary>
    private readonly IComparer<TPriority>? _comparer;

    /// <summary>The bounded capacity, or <c>-1</c> when the queue is unbounded.</summary>
    private readonly int _boundedCapacity;

    /// <summary>
    /// The default stickiness factor: <c>1</c> (resample every operation). Chosen so the default
    /// public contract matches the pre-stickiness behavior exactly, following the
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>
    /// <c>concurrencyLevel</c> convention of a safe default behind an explicit opt-in.
    /// </summary>
    private const int DefaultStickiness = 1;

    /// <summary>
    /// The stickiness factor <c>s</c>: the number of consecutive operations a thread reuses a sampled
    /// sub-queue selection before re-sampling (ESA 2021). <c>1</c> leaves the relaxed-dequeue contract
    /// unchanged; larger values trade dequeue quality (expected rank error scales to <c>s·(5/6)·n</c>)
    /// for throughput when per-operation overhead dominates. Resolved once in the core constructor.
    /// </summary>
    private readonly int _stickiness;

    /// <summary>
    /// The shared atomic capacity gate: the number of live reservations against
    /// <see cref="_boundedCapacity"/>. An enqueue increments it before selecting a sub-queue and
    /// rejects the element when the result exceeds the bound; every successful removal decrements
    /// it. The field is <i>only ever touched on the bounded path</i> (guarded by
    /// <c>_boundedCapacity &gt; 0</c>), so an unbounded queue runs no
    /// <see cref="System.Threading.Interlocked"/> instruction against it and it stays permanently
    /// zero. It is accessed only through interlocked or volatile operations, never a plain read or
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
    /// class with an explicit stickiness factor, a throughput/relaxation tuning hint modeled on the
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
    /// <c>-1</c> selects the default (<see cref="DefaultStickiness"/>). Note on relaxation: the
    /// relaxed <see cref="TryDequeue"/>'s expected rank error scales to <c>s·(5/6)·n</c>, so like the
    /// sub-queue count, the looseness a caller tolerates grows with this dial and compounds across
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
    /// Always call this constructor with named arguments. A positional <c>(int, int, null)</c>
    /// argument list binds to the public <c>(boundedCapacity, stickiness, comparer)</c> overload
    /// instead, because C# prefers the candidate that needs no default-argument substitution. That
    /// silently reinterprets the first two ints: <c>new ConcurrentPriorityQueue&lt;T, P&gt;(4, -1,
    /// null)</c> builds a bounded-to-4, default-sub-queue-count queue, not a 4-sub-queue unbounded one.
    /// </remarks>
    /// <param name="subQueueCount">
    /// The requested number of sub-queues; must be at least one. A value that is not already a
    /// power of two is rounded up to the next power of two so that two-choice index selection stays
    /// a single mask; values that are already powers of two (such as 1 and 4) are honored exactly.
    /// </param>
    /// <param name="boundedCapacity">The bounded capacity, or <c>-1</c> for an unbounded queue.</param>
    /// <param name="comparer">
    /// The priority comparer, or <see langword="null"/> to use <see cref="Comparer{T}.Default"/>.
    /// The value is normalized for storage and passed unchanged to each sub-queue, which
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

        // Funnel invariant: this internal ctor is the single point every overload chains through, but
        // some internal callers pass `boundedCapacity` unvalidated. Enforce the bound here too so a
        // contradictory value (0, or any value below the -1 unbounded sentinel) can never construct an
        // instance, regardless of caller. Public overloads already validated; this is idempotent.
        boundedCapacity = ValidateBoundedCapacityOrUnbounded(boundedCapacity);

        _stickiness = ResolveStickiness(stickiness);

        // Round up to a power of two so two-choice index selection is `rng & (count - 1)`. Values
        // that are already powers of two (1, 4, the default 4×ProcessorCount round-up) are returned
        // unchanged, so the internal-count contract honors them exactly.
        int count = (int)BitOperations.RoundUpToPowerOf2((uint)subQueueCount);

        // Comparer normalization, shared with SubQueue so queue-level ordering code can also
        // dual-path later (see PriorityComparerHelpers.InitializeComparer).
        _comparer = PriorityComparerHelpers.InitializeComparer(comparer);

        _boundedCapacity = boundedCapacity;
        _subQueueMask = count - 1;

        // DR-1: the occupancy bitmask is ceil(count/64) words, all zero (every sub-queue starts
        // empty, matching each SubQueue's initial EmptyFlag = 1). Allocated once here and shared by
        // reference with every sub-queue so transition writes and routing reads see the same array.
        _occupancy = new ulong[(count + 63) >> 6];

        _queues = new SubQueue<TElement, TPriority>[count];
        for (int i = 0; i < count; i++)
        {
            // Pass the ORIGINAL comparer: SubQueue performs the same normalization itself. Hand each
            // sub-queue its index and a reference to the shared occupancy array so its boundary-only
            // transition writes (DR-2) flip the right bit.
            _queues[i] = new SubQueue<TElement, TPriority>(comparer, i, _occupancy);
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
    /// Gets the bounded capacity of the queue, or <c>-1</c> when the queue is unbounded, the
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
    /// (<see cref="_boundedCount"/>), the number of live reservations against
    /// <see cref="_boundedCapacity"/>. On an unbounded queue this is permanently zero, because the
    /// unbounded path never increments the gate.
    /// </summary>
    internal int DebugBoundedCountForTest => Volatile.Read(ref _boundedCount);

    /// <summary>
    /// Maps a sub-queue index to its word in <see cref="_occupancy"/>: <c>i &gt;&gt; 6</c> (64 bits per
    /// word, a shift not a division, mirroring the <see cref="_subQueueMask"/> discipline; DR-1).
    /// </summary>
    /// <param name="index">The sub-queue index.</param>
    /// <returns>The occupancy word index <c>index &gt;&gt; 6</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int OccupancyWord(int index) => index >> 6;

    /// <summary>
    /// Maps a sub-queue index to its single-bit mask within its occupancy word:
    /// <c>1UL &lt;&lt; (i &amp; 63)</c> (DR-1).
    /// </summary>
    /// <param name="index">The sub-queue index.</param>
    /// <returns>A <see cref="ulong"/> with exactly the bit for <paramref name="index"/> set.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong OccupancyBit(int index) => 1UL << (index & 63);

    /// <summary>TEST-ONLY: exposes <see cref="OccupancyWord"/> for the indexing-math unit tests (DR-1).</summary>
    /// <param name="index">The sub-queue index.</param>
    /// <returns>The occupancy word index for <paramref name="index"/>.</returns>
    internal static int OccupancyWordForTest(int index) => OccupancyWord(index);

    /// <summary>TEST-ONLY: exposes <see cref="OccupancyBit"/> for the indexing-math unit tests (DR-1).</summary>
    /// <param name="index">The sub-queue index.</param>
    /// <returns>The single-bit mask for <paramref name="index"/>.</returns>
    internal static ulong OccupancyBitForTest(int index) => OccupancyBit(index);

    /// <summary>
    /// TEST-ONLY: a defensive copy of the occupancy bitmask words (DR-1/DR-2). Each word is read with
    /// a volatile read (an aligned <see cref="ulong"/> read is atomic on the supported 64-bit
    /// runtimes), giving a per-word-consistent (though not cross-word-atomic) snapshot — sufficient
    /// for the single-threaded transition-write assertions and the routing tests.
    /// </summary>
    internal ulong[] DebugOccupancyForTest
    {
        get
        {
            var snapshot = new ulong[_occupancy.Length];
            for (int i = 0; i < _occupancy.Length; i++)
            {
                snapshot[i] = Volatile.Read(ref _occupancy[i]);
            }

            return snapshot;
        }
    }

    /// <summary>
    /// TEST-ONLY: forces sub-queue <paramref name="index"/>'s occupancy bit to <c>1</c> without
    /// pushing an element, fabricating a <i>stale-set</i> bit (the bitmask reads occupied over an
    /// actually-empty sub-queue). Used to prove the staleness-safety contract (DR-4): routing must
    /// attempt the locked pop, observe <see cref="SubQueuePopStatus.Empty"/>, skip the bit and
    /// continue — never returning a bogus <see langword="true"/>. Uses the same atomic
    /// <see cref="Interlocked.Or(ref ulong, ulong)"/> the production transition write uses.
    /// </summary>
    /// <param name="index">The sub-queue index whose bit to force set.</param>
    internal void DebugForceSetOccupancyBitForTest(int index)
        => Interlocked.Or(ref _occupancy[OccupancyWord(index)], OccupancyBit(index));

    /// <summary>
    /// Releases one bounded-capacity reservation after a successful removal: when the queue is
    /// bounded, atomically decrements the shared gate (<see cref="_boundedCount"/>). Every
    /// successful-removal path funnels through this single release point: <c>TryPopFrom</c>'s
    /// success branch (covering both two-choice sampling and the verification scan) and
    /// <c>TryDequeueMin</c>'s success path. The reservation accounting therefore has exactly one
    /// decrement site, matching the one increment site in <c>EnqueueCore</c>. On an unbounded queue
    /// (<c>_boundedCapacity &lt; 0</c>) it is a no-op that runs no
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
    /// Validates a <c>boundedCapacity</c> argument for the stickiness constructor. Unlike
    /// <see cref="ValidateBoundedCapacity"/>, it also accepts the unbounded sentinel <c>-1</c> so an
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
    /// throws. This mirrors the <c>ConcurrentDictionary(concurrencyLevel)</c> validation convention
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
