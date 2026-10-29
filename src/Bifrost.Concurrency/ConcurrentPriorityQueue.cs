// =============================================================================
// <copyright file="ConcurrentPriorityQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Bifrost.Concurrency;

/// <summary>A thread-safe MultiQueue with lock-striped heaps and relaxed two-choice removal.</summary>
/// <typeparam name="TElement">The element type.</typeparam>
/// <typeparam name="TPriority">The priority type.</typeparam>
/// <remarks>
/// <para>Default stripes are RoundUpToPowerOf2(4 × ProcessorCount), stickiness is one, and
/// buffering is disabled. ProcessorCount is a sizing heuristic, not a bound on participating threads.
/// Operations use stripe locks and retries; the queue is neither lock-free nor wait-free.
/// A stalled lock holder can delay an operation indefinitely.</para>
/// <para>TryDequeue is relaxed. No deterministic rank-error or wall-clock waiting bound is promised.
/// The ESA 2025 stationary formula applies to an idealized deletion-only, with-replacement model;
/// this implementation samples distinct stripes and includes stickiness, buffers and fallback scans.
/// Measure ordering quality for the intended workload. Equal priorities are not FIFO-stable.</para>
/// <para>Use Create with explicit options for bounded capacity and tuning. Capacity is an item limit,
/// not an initial allocation size. Unbounded operations do not touch a global capacity counter.</para>
/// <para>A thrown comparer leaves the failing operation's resident contents and capacity reservation
/// unchanged. Other concurrent operations may proceed. Comparers must define a consistent ordering,
/// be safe for concurrent calls, and must not reenter or mutate this queue. Mutating stored priority
/// values is unsupported. Allocation failures and process-fatal failures are outside this guarantee.</para>
/// <para>Count, IsEmpty, inspection and enumeration are observations, not synchronization protocols.
/// Collection copies visit one stripe at a time and are not point-in-time snapshots.</para>
/// </remarks>
[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(ConcurrentPriorityQueueDebugView<,>))]
public sealed partial class ConcurrentPriorityQueue<TElement, TPriority>
{
    /// <summary>Creates a queue with explicit capacity and algorithm tuning.</summary>
    /// <param name="options">Settings copied at construction.</param>
    /// <returns>A new queue.</returns>
    public static ConcurrentPriorityQueue<TElement, TPriority> Create(ConcurrentPriorityQueueOptions<TPriority> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Capacity is <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Stickiness < 1) throw new ArgumentOutOfRangeException(nameof(options));
        return new(subQueueCount: options.SubQueueCount ?? s_defaultSubQueueCount,
            boundedCapacity: options.Capacity ?? -1, comparer: options.Comparer,
            stickiness: options.Stickiness, bufferCapacity: options.BufferCapacity);
    }

    /// <summary>Gets the maximum item count, or null for an unbounded queue.</summary>
    public int? Capacity => _boundedCapacity > 0 ? _boundedCapacity : null;

    /// <summary>Gets the actual power-of-two stripe count.</summary>
    public int SubQueueCount => _queues.Length;

    /// <summary>Gets the configured sampling stickiness.</summary>
    public int Stickiness => _stickiness;

    /// <summary>Gets the configured per-stripe buffer capacity.</summary>
    public int BufferCapacity => _bufferCapacity;

    /// <summary>
    /// The default number of sub-queues: <c>RoundUpToPowerOf2(4 × ProcessorCount)</c>. Computed
    /// once into a <c>static readonly</c> field so it folds to a JIT constant; the multiplier 4 is a
    /// well-established quality/throughput ratio for relaxed priority queues, and rounding up to a
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
    /// Per-instance occupancy bitmask: one bit per sub-queue, <c>(n + 63) &gt;&gt; 6</c> words long.
    /// Bit <c>i</c> (word <c>i &gt;&gt; 6</c>, position <c>i &amp; 63</c>) is set while sub-queue <c>i</c>
    /// has published itself non-empty and cleared once it publishes empty. The array is allocated
    /// once in the core constructor and shared by reference with every
    /// <see cref="SubQueue{TElement, TPriority}"/>, each of which knows its own index and flips its
    /// bit under its own <c>SyncLock</c> on a boundary crossing via
    /// <see cref="Interlocked.Or(ref ulong, ulong)"/> / <see cref="Interlocked.And(ref ulong, ulong)"/>.
    /// The atomics work per word, so two sub-queues that share a word never clobber each other's bit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dequeue path reads this lock-free as a routing hint once its two-choice sampling budget is
    /// spent: instead of falling to the O(n) verification scan, the consumer reads the occupancy
    /// words and jumps straight to a populated sub-queue via
    /// <see cref="BitOperations.TrailingZeroCount(ulong)"/>. It is only a hint. The verification scan
    /// stays the one authority allowed to return <see langword="false"/>, so a stale read costs at
    /// most a wasted routing attempt, never a wrong answer.
    /// </para>
    /// <para>
    /// Under dense load the bitmask is neither written (sub-queues rarely hit size zero) nor read
    /// (sampling lands a pop within budget), so it stays dormant in cache and adds no allocation or
    /// contention to the dequeue path. Unused high bits in the last word of a non-multiple-of-64
    /// sub-queue count stay zero and are never read as occupied.
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
    /// sub-queue selection before re-sampling. <c>1</c> leaves the relaxed-dequeue contract unchanged;
    /// larger values trade dequeue quality (rank error can increase) for
    /// throughput when per-operation overhead dominates. Resolved once in the core constructor.
    /// </summary>
    private readonly int _stickiness;

    /// <summary>
    /// The buffer capacity <c>C ∈ [0, SubQueue.BufferCapacityMax]</c> threaded into every sub-queue:
    /// <c>0</c> (the default) disables buffering, leaving Enqueue/Dequeue bit-exact with the unbuffered
    /// queue; <c>1..16</c> activates the per-sub-queue insertion/deletion buffers with that cap. Resolved
    /// and validated once in the core constructor.
    /// </summary>
    private readonly int _bufferCapacity;

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
    internal ConcurrentPriorityQueue(int boundedCapacity, IComparer<TPriority>? comparer = null)
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
    /// The stickiness factor <c>s</c>: a thread reuses its sampled sub-queue selection for <c>s</c>
    /// consecutive operations before re-sampling, hiding the cost of re-sampling. Must be at least one;
    /// <c>-1</c> selects the default (<see cref="DefaultStickiness"/>). Raise it (to a small value such
    /// as four) only in the low-thread regime, where the fixed sampling cost is not yet buried under
    /// contention and a higher <c>s</c> lifts throughput; under real contention leave it at one, which
    /// keeps a pop closest to the true minimum. The price is dequeue quality: the relaxed
    /// <see cref="TryDequeue"/>'s rank error can increase, so a pop grows looser
    /// as this dial rises, on top of how that looseness already grows with the core count. Keep it small
    /// — one or four are the safe choices.
    /// </param>
    /// <param name="comparer">
    /// The priority comparer, or <see langword="null"/> to use <see cref="Comparer{T}.Default"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="boundedCapacity"/> is zero or less than <c>-1</c>, or
    /// <paramref name="stickiness"/> is less than one and not the <c>-1</c> default sentinel.
    /// </exception>
    internal ConcurrentPriorityQueue(int boundedCapacity, int stickiness, IComparer<TPriority>? comparer = null)
        : this(s_defaultSubQueueCount, ValidateBoundedCapacityOrUnbounded(boundedCapacity), comparer, stickiness)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentPriorityQueue{TElement, TPriority}"/>
    /// class with an explicit stickiness factor and a <c>bufferCapacity</c>, the opt-in per-sub-queue
    /// buffering dial (default-off in every other overload).
    /// </summary>
    /// <param name="boundedCapacity">
    /// The maximum number of elements the queue may hold (a positive value), or <c>-1</c> for an
    /// unbounded queue.
    /// </param>
    /// <param name="stickiness">
    /// The stickiness factor <c>s</c>; must be at least one, or <c>-1</c> for the default
    /// (<see cref="DefaultStickiness"/>). See the three-argument overload for the relaxation note.
    /// </param>
    /// <param name="bufferCapacity">
    /// The buffer capacity <c>C ∈ [0, SubQueue.BufferCapacityMax]</c>: <c>0</c> (the default) disables
    /// buffering and leaves Enqueue/Dequeue bit-exact with the unbuffered queue; <c>1..16</c> gives each
    /// sub-queue an insertion and a sorted deletion buffer of that size, so the deep heap is touched only
    /// about once per <c>C</c> operations rather than on every one. Enable it (16 is the usual choice)
    /// for low thread counts, drain-heavy workloads, or reference-type elements with deep heaps, where it
    /// lowers single-thread latency; on a saturated machine running a cheap value-type mix it is roughly
    /// neutral, sometimes slightly slower, so it stays off by default. The buffers wrap the heap rather
    /// than replacing it, and add no per-operation allocation either way. This is a distinct
    /// four-argument overload from the <c>(boundedCapacity, stickiness, comparer)</c> one, so adding it
    /// re-binds no existing positional call site.
    /// </param>
    /// <param name="comparer">
    /// The priority comparer, or <see langword="null"/> to use <see cref="Comparer{T}.Default"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="boundedCapacity"/> is zero or less than <c>-1</c>; <paramref name="stickiness"/>
    /// is less than one and not the <c>-1</c> default sentinel; or <paramref name="bufferCapacity"/> is
    /// outside <c>[0, SubQueue.BufferCapacityMax]</c>.
    /// </exception>
    internal ConcurrentPriorityQueue(int boundedCapacity, int stickiness, int bufferCapacity, IComparer<TPriority>? comparer = null)
        : this(s_defaultSubQueueCount, ValidateBoundedCapacityOrUnbounded(boundedCapacity), comparer, stickiness, ValidateBufferCapacity(bufferCapacity))
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
    /// <param name="bufferCapacity">
    /// The buffer capacity <c>C ∈ [0, SubQueue.BufferCapacityMax]</c>; a trailing optional so the
    /// existing call sites keep compiling against the default <c>0</c> (buffering off). Validated here
    /// idempotently and threaded into every sub-queue.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="subQueueCount"/> is less than one; <paramref name="stickiness"/> is less than
    /// one and not the <c>-1</c> default sentinel; or <paramref name="bufferCapacity"/> is outside
    /// <c>[0, SubQueue.BufferCapacityMax]</c>.
    /// </exception>
    internal ConcurrentPriorityQueue(int subQueueCount, int boundedCapacity, IComparer<TPriority>? comparer, int stickiness = DefaultStickiness, int bufferCapacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(subQueueCount, 1);

        // Funnel invariant: this internal ctor is the single point every overload chains through, but
        // some internal callers pass `boundedCapacity` unvalidated. Enforce the bound here too so a
        // contradictory value (0, or any value below the -1 unbounded sentinel) can never construct an
        // instance, regardless of caller. Public overloads already validated; this is idempotent.
        boundedCapacity = ValidateBoundedCapacityOrUnbounded(boundedCapacity);

        // Idempotent for the public buffered overload (already validated); the authoritative check for
        // any internal caller that pins a sub-queue count with buffering on.
        _bufferCapacity = ValidateBufferCapacity(bufferCapacity);

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

        // ceil(count/64) words, all zero to start: every sub-queue begins empty, matching each
        // SubQueue's initial EmptyFlag = 1. Allocated once here and shared by reference with every
        // sub-queue so transition writes and routing reads see the same array.
        _occupancy = new ulong[(count + 63) >> 6];

        _queues = new SubQueue<TElement, TPriority>[count];
        for (int i = 0; i < count; i++)
        {
            // Pass the ORIGINAL comparer: SubQueue performs the same normalization itself. Hand each
            // sub-queue its index and a reference to the shared occupancy array so its boundary-only
            // transition writes flip the right bit, plus the resolved buffer capacity (0 = unbuffered).
            _queues[i] = new SubQueue<TElement, TPriority>(comparer, i, _occupancy, _bufferCapacity);
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
    internal int BoundedCapacity => _boundedCapacity;

    /// <summary>Gets the number of sub-queues. Exposed for tests; equals <c>_queues.Length</c>.</summary>
    internal int SubQueueCountForTest => _queues.Length;

    /// <summary>Gets the resolved stickiness factor <c>s</c>. Exposed for tests.</summary>
    internal int StickinessForTest => _stickiness;

    /// <summary>Gets the resolved buffer capacity <c>C</c> (<c>0</c> when buffering is off). Exposed for tests.</summary>
    internal int BufferCapacityForTest => _bufferCapacity;

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
    /// word, a shift rather than a division, the same way <see cref="_subQueueMask"/> works).
    /// </summary>
    /// <param name="index">The sub-queue index.</param>
    /// <returns>The occupancy word index <c>index &gt;&gt; 6</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int OccupancyWord(int index) => index >> 6;

    /// <summary>
    /// Maps a sub-queue index to its single-bit mask within its occupancy word:
    /// <c>1UL &lt;&lt; (i &amp; 63)</c>.
    /// </summary>
    /// <param name="index">The sub-queue index.</param>
    /// <returns>A <see cref="ulong"/> with the bit for <paramref name="index"/> set.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong OccupancyBit(int index) => 1UL << (index & 63);

    /// <summary>TEST-ONLY: exposes <see cref="OccupancyWord"/> for the indexing-math unit tests.</summary>
    /// <param name="index">The sub-queue index.</param>
    /// <returns>The occupancy word index for <paramref name="index"/>.</returns>
    internal static int OccupancyWordForTest(int index) => OccupancyWord(index);

    /// <summary>TEST-ONLY: exposes <see cref="OccupancyBit"/> for the indexing-math unit tests.</summary>
    /// <param name="index">The sub-queue index.</param>
    /// <returns>The single-bit mask for <paramref name="index"/>.</returns>
    internal static ulong OccupancyBitForTest(int index) => OccupancyBit(index);

    /// <summary>
    /// TEST-ONLY: a defensive copy of the occupancy bitmask words. Each word is read with a volatile
    /// read (an aligned <see cref="ulong"/> read is atomic on the supported 64-bit runtimes), giving a
    /// per-word-consistent (though not cross-word-atomic) snapshot. That is enough for the
    /// single-threaded transition-write assertions and the routing tests.
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
    /// actually-empty sub-queue). This is how the tests exercise staleness safety: routing has to
    /// attempt the locked pop, see <see cref="SubQueuePopStatus.Empty"/>, then skip the bit and carry
    /// on rather than return a bogus <see langword="true"/>. Uses the same atomic
    /// <see cref="Interlocked.Or(ref ulong, ulong)"/> as the production transition write.
    /// </summary>
    /// <param name="index">The sub-queue index whose bit to force set.</param>
    internal void DebugForceSetOccupancyBitForTest(int index)
        => Interlocked.Or(ref _occupancy[OccupancyWord(index)], OccupancyBit(index));

    /// <summary>
    /// Releases one bounded-capacity reservation after a successful removal: when the queue is
    /// bounded, atomically decrements the shared gate (<see cref="_boundedCount"/>). Every
    /// successful-removal path funnels through this single release point: <c>TryPopFrom</c>'s
    /// success branch (covering both two-choice sampling and the verification scan) and
    /// <c>TryDequeueBestObserved</c>'s success path. The reservation accounting therefore has exactly one
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
    /// Validates a <c>bufferCapacity</c> argument against the buffering range
    /// <c>[0, SubQueue.BufferCapacityMax]</c> (mirroring <see cref="ValidateBoundedCapacity"/>) and
    /// returns it unchanged for inline forwarding. <c>0</c> means buffering off; <c>1..16</c> activates
    /// it with that logical cap. The throw message names the compile-time max so the caller learns the
    /// ceiling (the inline storage is compile-time-fixed; a larger cap also degrades dequeue quality).
    /// </summary>
    /// <param name="bufferCapacity">The requested logical buffer capacity.</param>
    /// <returns>The validated <paramref name="bufferCapacity"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bufferCapacity"/> is negative or greater than
    /// <see cref="SubQueue{TElement, TPriority}.BufferCapacityMax"/>.
    /// </exception>
    private static int ValidateBufferCapacity(int bufferCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bufferCapacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bufferCapacity, SubQueue<TElement, TPriority>.BufferCapacityMax);
        return bufferCapacity;
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
