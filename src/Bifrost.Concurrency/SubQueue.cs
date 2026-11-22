// =============================================================================
// <copyright file="SubQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Bifrost.Concurrency;

/// <summary>
/// A single MultiQueue sub-queue. This type owns the sub-queue's writer lock and the cached-top
/// seqlock, and the sequential arity-4 implicit min-heap with dual devirtualized comparer
/// paths. The heap operations are sequential; composing them under the lock with a
/// top publication is a later task.
/// </summary>
/// <typeparam name="TElement">The element type stored alongside each priority.</typeparam>
/// <typeparam name="TPriority">The priority type ordered by the queue's comparer.</typeparam>
/// <remarks>
/// <para>
/// The two-choice dequeue samples two random sub-queues and compares their minimum priorities
/// <i>without locking</i>. <typeparamref name="TPriority"/> is an arbitrary generic, and a
/// multi-word struct cannot be read atomically, so an unsynchronized read could observe a torn
/// value. Each sub-queue therefore publishes its top through a seqlock: writers stamp
/// <see cref="SubQueueHeader.TopVersion"/> odd, mutate the cached top and empty flag, then stamp
/// it even; readers validate that the version was even and unchanged around their copy, and
/// otherwise retry or report "unknown".
/// </para>
/// <para>
/// <see cref="TryReadTop"/> returns either a recent <c>(top, empty)</c> snapshot or <c>false</c>,
/// meaning "unknown, resample another queue". Callers must never depend on <i>how</i> the snapshot
/// is obtained (a lock-free seqlock today; the documented fallback is a <c>TryEnter</c>-read that
/// preserves the same contract), only on the contract itself.
/// </para>
/// <para>
/// Emptiness is a flag word, never a sentinel: <see cref="SubQueueHeader.EmptyFlag"/> travels
/// inside the seqlock-protected region, so emptiness needs no <c>default(TPriority)</c> sentinel
/// and the read path never invokes <see cref="IComparer{T}"/> (pinned by the
/// <c>TryReadTop_NeverInvokesComparer</c> test).
/// </para>
/// </remarks>
internal sealed class SubQueue<TElement, TPriority>
{
    /// <summary>
    /// The compile-time-fixed maximum buffer capacity, <c>16</c> — the buffering optimum
    /// (Williams &amp; Sanders measure the relaxed dequeue's rank-error degrading past this point, so a
    /// larger value would trade quality for throughput). It fixes the inline storage size of
    /// <see cref="SubQueueBuffer{TElement, TPriority}"/> at compile time and bounds the logical
    /// <c>bufferCapacity</c> knob's <c>[0, 16]</c> range. Bumping it is a one-line const change plus a
    /// rebuild; it is deliberately not a runtime parameter (the C++ reference also compile-time-fixes it).
    /// </summary>
    internal const int BufferCapacityMax = 16;

    /// <summary>
    /// The bound on seqlock read attempts before <see cref="TryReadTop"/> reports "unknown".
    /// Two-choice callers tolerate unknown results by resampling, so a small bound keeps the
    /// read path's worst case short instead of spinning against a stalled or hot writer.
    /// </summary>
    private const int ReadRetryLimit = 8;

    /// <summary>
    /// The heap's first allocation size, chosen on the initial push. Sixteen is one full arity-4
    /// level below the root (1 + 4 + ... fills to depth two within a couple of growths) and keeps
    /// the common small sub-queue off a second allocation; storage then doubles when full.
    /// </summary>
    private const int InitialCapacity = 16;

    /// <summary>
    /// The comparer used by heap ordering. The seqlock paths in this type never
    /// invoke it: the cached top is copied raw and emptiness is a flag word.
    /// </summary>
    private readonly IComparer<TPriority>? _comparer;

    /// <summary>
    /// This sub-queue's index in the owning queue's sub-queue array, used to address its bit in the
    /// shared occupancy bitmask: word <c>_index &gt;&gt; 6</c>, position <c>_index &amp; 63</c>.
    /// </summary>
    private readonly int _index;

    /// <summary>
    /// A reference to the owning queue's shared occupancy bitmask. This sub-queue flips
    /// <i>only its own bit</i> (<c>_occupancy[_index &gt;&gt; 6]</c>, mask <c>1UL &lt;&lt; (_index &amp; 63)</c>)
    /// and only on an empty&#8596;non-empty boundary crossing while <see cref="SyncLock"/> is held,
    /// via <see cref="Interlocked.Or(ref ulong, ulong)"/> / <see cref="Interlocked.And(ref ulong, ulong)"/>
    /// so a neighbouring sub-queue sharing the same 64-bit word never loses an update.
    /// </summary>
    private readonly ulong[] _occupancy;

    /// <summary>
    /// TEST-ONLY instrumentation: the number of times this sub-queue wrote its occupancy bit (a set
    /// or a clear). Because the writes are boundary-only, this increments exactly once per
    /// empty&#8596;non-empty crossing and never on a push onto a populated heap or a non-last pop.
    /// It is incremented inside <see cref="SetOccupancyBit"/>/<see cref="ClearOccupancyBit"/>, both
    /// of which run under <see cref="SyncLock"/>, so the writes are serialized; a single-threaded
    /// test reads it directly via <see cref="DebugOccupancyWriteCountForTest"/>.
    /// </summary>
    // CS0649: when BIFROST_TEST_HOOKS is undefined (the shipped package) these fields are read by their
    // getters but never assigned, since the only writers are the gated bodies of the Count… hooks. The
    // fields and …ForTest getters stay UNGATED so the test project still compiles against the stripped
    // publish library; only the increment SITES are gated (see the buffered-counter region below).
#pragma warning disable CS0649
    private long _debugOccupancyWriteCount;

    /// <summary>TEST-ONLY: the number of insertion-buffer flushes into the heap.</summary>
    private long _debugBufferFlushCount;

    /// <summary>TEST-ONLY: the number of deletion-buffer refills from the heap.</summary>
    private long _debugBufferRefillCount;

    /// <summary>TEST-ONLY: the number of pops served straight from <c>D.front()</c> (buffered-pop hits).</summary>
    private long _debugBufferPopHitCount;

    /// <summary>TEST-ONLY: the number of direct-to-<c>D</c> seeds of an otherwise-empty structure.</summary>
    private long _debugBufferDirectToDeletionCount;

    /// <summary>TEST-ONLY: the number of <c>max(D)</c> eviction cascades on a full-<c>D</c> sorted insert.</summary>
    private long _debugBufferEvictionCount;
#pragma warning restore CS0649

    /// <summary>The cached top priority, isolated on its own cache line (see <see cref="PaddedTopSlot{TPriority}"/>).</summary>
    private readonly PaddedTopSlot<TPriority> _cachedTop;

    /// <summary>The padded hot fields: striped count, seqlock version, empty flag (see <see cref="SubQueueHeader"/>).</summary>
    private SubQueueHeader _header;

    /// <summary>
    /// The implicit arity-4 min-heap storage: an inline array of <c>(element, priority)</c>
    /// entries laid out exactly like <see cref="PriorityQueue{TElement, TPriority}"/> (which also
    /// names this array <c>_nodes</c>), where the children of node <c>i</c> live at
    /// <c>4i+1 .. 4i+4</c> and its parent at <c>(i-1) &gt;&gt; 2</c>. Starts empty and is allocated
    /// at <see cref="InitialCapacity"/> on the first push.
    /// </summary>
    private (TElement Element, TPriority Priority)[] _nodes;

    /// <summary>The number of live entries in <see cref="_nodes"/> (the heap occupies <c>[0, _size)</c>).</summary>
    private int _size;

    /// <summary>
    /// The logical buffer capacity <c>C ∈ [0, BufferCapacityMax]</c>: <c>0</c> disables buffering (the
    /// push/pop paths bypass the buffers entirely and operate directly on the arity-4 heap, bit-exact
    /// with the pre-feature behavior); <c>1..16</c> caps the logical size of each buffer within the
    /// fixed-16 inline storage. Readonly: set once at construction and never mutated.
    /// </summary>
    private readonly int _bufferCapacity;

    /// <summary>
    /// The unsorted insertion buffer <c>I</c>: pushes that are not small enough to enter
    /// the sorted deletion buffer land here; when it fills (<see cref="_insertionCount"/> reaches
    /// <see cref="_bufferCapacity"/>) it flushes wholesale into the arity-4 heap. Inline storage,
    /// mutated only under <see cref="SyncLock"/>. Untouched when <see cref="_bufferCapacity"/> is 0.
    /// </summary>
    private SubQueueBuffer<TElement, TPriority> _insertion;

    /// <summary>The number of live entries in <see cref="_insertion"/> (occupies <c>[0, _insertionCount)</c>).</summary>
    private int _insertionCount;

    /// <summary>
    /// The sorted deletion buffer <c>D</c>: holds the smallest resident entries in
    /// ascending priority order. <c>D.front()</c> (slot 0) is the sub-queue minimum and the value the
    /// seqlock publishes; pops remove it. It is refilled from the heap when it empties. Inline storage,
    /// mutated only under <see cref="SyncLock"/>. Untouched when <see cref="_bufferCapacity"/> is 0.
    /// </summary>
    private SubQueueBuffer<TElement, TPriority> _deletion;

    /// <summary>The number of live entries in <see cref="_deletion"/> (occupies <c>[0, _deletionCount)</c>).</summary>
    private int _deletionCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubQueue{TElement, TPriority}"/> class.
    /// The initial state is a valid, readable, <i>empty</i> publication: version 0 (even) with
    /// the empty flag set, so a reader that samples a brand-new sub-queue gets a stable
    /// <c>(empty: true)</c> snapshot without any writer having run.
    /// </summary>
    /// <param name="comparer">
    /// The priority comparer retained for the heap tasks; <see langword="null"/> selects the
    /// devirtualized <see cref="Comparer{T}.Default"/> path at the queue level.
    /// </param>
    /// <param name="index">
    /// This sub-queue's index in the owning queue's sub-queue array; addresses its bit in
    /// <paramref name="occupancy"/>.
    /// </param>
    /// <param name="occupancy">
    /// The owning queue's shared occupancy bitmask. This sub-queue flips only its own bit, under its
    /// lock, on an empty&#8596;non-empty crossing. The array is shared by reference, never copied.
    /// </param>
    /// <param name="bufferCapacity">
    /// The buffer capacity <c>C ∈ [0, BufferCapacityMax]</c>; a trailing optional
    /// so the existing three-argument call sites keep compiling against the default. <c>0</c> (the
    /// default) disables buffering, leaving the push/pop paths bit-exact with the pre-feature heap
    /// behavior; <c>1..16</c> activates the buffered path with that logical cap. The owning queue
    /// validates the range before forwarding; this constructor debug-asserts it.
    /// </param>
    /// <remarks>
    /// <para>
    /// The comparer goes through <c>PriorityComparerHelpers.InitializeComparer</c>, the same
    /// normalization that the queue shell uses. A stored <see langword="null"/> selects the
    /// devirtualized <c>Comparer&lt;TPriority&gt;.Default</c> path in the hot heap methods.
    /// </para>
    /// <para>
    /// The <c>_cachedTop</c> assignment must call the explicit parameterless
    /// <see cref="PaddedTopSlot{TPriority}"/> constructor, because that constructor allocates the
    /// padded backing array. A change to <c>default</c> or <c>default(T)</c> (for example, to satisfy
    /// SA1129) leaves the backing array null, and the seqlock then throws a
    /// <see cref="NullReferenceException"/> on the first publish.
    /// </para>
    /// <para>
    /// The constructor sets only <c>_header.EmptyFlag</c>. <c>TopVersion</c> keeps its default of 0,
    /// which is even and so means "stable".
    /// </para>
    /// </remarks>
    internal SubQueue(IComparer<TPriority>? comparer, int index, ulong[] occupancy, int bufferCapacity = 0)
    {
        Debug.Assert(
            bufferCapacity is >= 0 and <= BufferCapacityMax,
            "bufferCapacity must be in [0, BufferCapacityMax]; the owning queue validates before forwarding.");
        _comparer = PriorityComparerHelpers.InitializeComparer(comparer);

        _index = index;
        _occupancy = occupancy;
        _bufferCapacity = bufferCapacity;

        _nodes = [];

#pragma warning disable SA1129
        _cachedTop = new PaddedTopSlot<TPriority>();
#pragma warning restore SA1129
        _header.EmptyFlag = 1;
    }

    /// <summary>
    /// Gets the lock that serializes writers of this sub-queue. Every mutation (heap operations
    /// and <see cref="PublishTop"/>) must run while this lock is held; readers of the cached top
    /// never take it. Exposed so callers can compose multi-step critical sections (push + publish)
    /// under a single acquisition.
    /// </summary>
    internal Lock SyncLock { get; } = new();

    /// <summary>
    /// Gets a value indicating whether the heap dispatches to the devirtualized default-comparer
    /// path. True exactly when the stored comparer is null, which the constructor
    /// establishes only for value-type priorities whose effective comparer is
    /// <see cref="Comparer{T}.Default"/>. Exposed for the comparer-dispatch tests.
    /// </summary>
    internal bool UsesDefaultComparerPath => _comparer is null;

    /// <summary>
    /// Gets this sub-queue's striped element count via a volatile read. Cross-queue consumers
    /// (<c>Count</c>/<c>IsEmpty</c>) sum or short-circuit over these snapshots.
    /// </summary>
    internal int VolatileCount => Volatile.Read(ref _header.Count);

    /// <summary>Gets the number of entries currently in the heap.</summary>
    internal int HeapSize => _size;

    /// <summary>
    /// Gets the logical buffer capacity <c>C</c> this sub-queue was constructed with (<c>0</c> when
    /// buffering is disabled). Exposed for the knob-default and bypass tests.
    /// </summary>
    internal int BufferCapacityForTest => _bufferCapacity;

    /// <summary>TEST-ONLY: the number of live entries in the insertion buffer <c>I</c>.</summary>
    internal int InsertionCountForTest => _insertionCount;

    /// <summary>TEST-ONLY: the number of live entries in the sorted deletion buffer <c>D</c>.</summary>
    internal int DeletionCountForTest => _deletionCount;

    /// <summary>
    /// TEST-ONLY: reads the current seqlock version so tests can assert when a mutation did
    /// (or deliberately did not) republish the cached top.
    /// </summary>
    internal uint DebugTopVersionForTest => Volatile.Read(ref _header.TopVersion);

    /// <summary>
    /// TEST-ONLY: the count of occupancy-bit writes (set + clear) this sub-queue has performed.
    /// Being boundary-only, it increments once per empty&#8596;non-empty crossing and stays frozen
    /// across pushes onto a populated heap and non-last pops.
    /// </summary>
    internal long DebugOccupancyWriteCountForTest => Volatile.Read(ref _debugOccupancyWriteCount);

    /// <summary>TEST-ONLY: the count of insertion-buffer flushes into the heap.</summary>
    internal long DebugBufferFlushCountForTest => Volatile.Read(ref _debugBufferFlushCount);

    /// <summary>TEST-ONLY: the count of deletion-buffer refills from the heap.</summary>
    internal long DebugBufferRefillCountForTest => Volatile.Read(ref _debugBufferRefillCount);

    /// <summary>TEST-ONLY: the count of pops served straight from <c>D.front()</c> (buffered-pop hits).</summary>
    internal long DebugBufferPopHitCountForTest => Volatile.Read(ref _debugBufferPopHitCount);

    /// <summary>TEST-ONLY: the count of direct-to-<c>D</c> seeds of an otherwise-empty structure.</summary>
    internal long DebugBufferDirectToDeletionCountForTest => Volatile.Read(ref _debugBufferDirectToDeletionCount);

    /// <summary>TEST-ONLY: the count of <c>max(D)</c> eviction cascades on a full-<c>D</c> sorted insert.</summary>
    internal long DebugBufferEvictionCountForTest => Volatile.Read(ref _debugBufferEvictionCount);

    /// <summary>
    /// Publishes a new cached top (or the empty state) through the seqlock. Must be called with
    /// <see cref="SyncLock"/> held; the lock guarantees a single writer, so version arithmetic
    /// needs no interlocked operations.
    /// </summary>
    /// <param name="top">The new minimum priority; ignored when <paramref name="empty"/> is set.</param>
    /// <param name="empty">Whether the sub-queue is now empty.</param>
    /// <remarks>
    /// <para>
    /// Memory-model argument (see <c>memmodel.md</c>):
    /// </para>
    /// <list type="number">
    /// <item><see cref="Volatile.Write{T}(ref T, T)"/> of the odd version has release semantics:
    /// "the effects of a volatile write will not be observable before effects of all previous
    /// reads and writes". That alone, however, does <b>not</b> stop the <i>following</i> data
    /// writes from becoming observable before the odd stamp (store–store reordering is real on
    /// arm64).</item>
    /// <item><see cref="Volatile.WriteBarrier"/> closes that hole: it is a release fence
    /// that "applies to all following writes", so the slot and flag writes below it cannot be
    /// observed before the odd stamp above it. A reader that sees the pre-write (even) version
    /// after copying data therefore cannot have read any of this writer's partial data.</item>
    /// <item>The final <see cref="Volatile.Write{T}(ref T, T)"/> of the even version is a release:
    /// all data writes are observable no later than the even stamp. A reader that observes the
    /// even stamp and then re-validates it unchanged has read fully published data.</item>
    /// </list>
    /// <para>
    /// Torn intermediate states are permitted to exist; readers discard them via version
    /// validation. Object references inside <typeparamref name="TPriority"/> are themselves
    /// pointer-aligned and read/written atomically (<c>memmodel.md</c>, "Atomic memory accesses"),
    /// so a discarded torn read can mix <i>stale</i> field values but can never fabricate an
    /// invalid reference.
    /// </para>
    /// <para>
    /// When publishing empty, the slot is cleared for reference-containing priorities so the
    /// cached copy does not keep an otherwise-dead object reachable; the flag word, not the slot
    /// value, is what readers consult for emptiness.
    /// </para>
    /// <para>
    /// The lock guarantees a single writer, so the plain read of <c>_header.TopVersion</c> into
    /// <c>version</c> cannot race another writer. The steps of the list map to the code as follows:
    /// step 1 is the <c>version + 1u</c> stamp that announces "writer in progress", step 2 is
    /// <see cref="Volatile.WriteBarrier"/>, and step 3 is the <c>version + 2u</c> stamp.
    /// </para>
    /// </remarks>
    internal void PublishTop(in TPriority top, bool empty)
    {
        Debug.Assert(SyncLock.IsHeldByCurrentThread, "PublishTop requires the sub-queue lock.");

        uint version = _header.TopVersion;

        Volatile.Write(ref _header.TopVersion, version + 1u);

        Volatile.WriteBarrier();

        if (!empty)
        {
            _cachedTop.Set(top);
        }
        else if (RuntimeHelpers.IsReferenceOrContainsReferences<TPriority>())
        {
            _cachedTop.Set(default!);
        }

        _header.EmptyFlag = empty ? 1 : 0;

        Volatile.Write(ref _header.TopVersion, version + 2u);
    }

    /// <summary>
    /// Attempts a lock-free, version-validated read of the cached top.
    /// </summary>
    /// <param name="top">The cached minimum priority, when the read validates and the sub-queue is non-empty.</param>
    /// <param name="empty">Whether the sub-queue published itself as empty.</param>
    /// <returns>
    /// <see langword="true"/> with a recent <c>(top, empty)</c> snapshot; or <see langword="false"/>
    /// meaning "unknown, resample another queue" after <see cref="ReadRetryLimit"/> attempts
    /// raced with writers. Never blocks and never invokes the comparer.
    /// </returns>
    /// <remarks>
    /// Memory-model argument (see <c>memmodel.md</c>): the leading
    /// <see cref="Volatile.Read{T}(ref readonly T)"/> has acquire semantics ("no read or write that
    /// is later in the program order may be speculatively executed ahead of a volatile read"), so the
    /// slot/flag copies cannot float above the first version check. <see cref="Volatile.ReadBarrier"/>
    /// is an acquire fence that "applies to all prior reads", so the slot/flag copies cannot sink
    /// below it, hence not below the second version check either. If both checks observe the same
    /// even version, no writer's window overlapped the copy, so the copy is untorn. The copies into
    /// <c>candidate</c> and <c>emptyFlag</c> are plain reads, and <c>candidate</c> can be torn when a
    /// writer overlaps. The re-check of the version into <c>after</c> discards exactly those cases.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadTop(out TPriority top, out bool empty)
    {
        SpinWait spinner = default;

        for (int attempt = 0; attempt < ReadRetryLimit; attempt++)
        {
            uint before = Volatile.Read(ref _header.TopVersion);

            if ((before & 1u) == 0)
            {
                TPriority candidate = _cachedTop.Get();
                int emptyFlag = _header.EmptyFlag;

                Volatile.ReadBarrier();

                uint after = Volatile.Read(ref _header.TopVersion);

                if (before == after)
                {
                    top = candidate;
                    empty = emptyFlag != 0;
                    return true;
                }
            }

            spinner.SpinOnce();
        }

        top = default!;
        empty = false;
        return false;
    }

    /// <summary>
    /// Reads a stable seqlock version and emptiness flag. Used to confirm that a scan's
    /// empty observations share one instant: the version is unchanged across two reads
    /// and the flag is empty.
    /// </summary>
    /// <param name="version">The even seqlock version when the read validates.</param>
    /// <param name="empty">Whether the sub-queue published itself as empty.</param>
    /// <returns><see langword="true"/> when a stable observation was taken.</returns>
    internal bool TryReadStableObservation(out uint version, out bool empty)
    {
        SpinWait spinner = default;

        for (int attempt = 0; attempt < ReadRetryLimit; attempt++)
        {
            uint before = Volatile.Read(ref _header.TopVersion);
            if ((before & 1u) == 0)
            {
                int emptyFlag = _header.EmptyFlag;
                Volatile.ReadBarrier();
                uint after = Volatile.Read(ref _header.TopVersion);
                if (before == after)
                {
                    version = before;
                    empty = emptyFlag != 0;
                    return true;
                }
            }

            spinner.SpinOnce();
        }

        version = 0;
        empty = false;
        return false;
    }

    /// <summary>
    /// TEST-ONLY: forces the seqlock version odd, simulating a writer that never completes, so
    /// tests can pin the bounded-retry "unknown" behaviour of <see cref="TryReadTop"/>.
    /// </summary>
    internal void DebugForceOddVersionForTest()
        => Volatile.Write(ref _header.TopVersion, _header.TopVersion | 1u);

    /// <summary>
    /// Pushes an <c>(element, priority)</c> entry onto the heap, growing the backing store when
    /// full, then sifting the new entry up to restore the arity-4 min-heap invariant.
    /// </summary>
    /// <param name="element">The element to store.</param>
    /// <param name="priority">The priority that orders the entry.</param>
    /// <returns>Whether this entry became the root (strictly smaller than the old root, or first entry).</returns>
    internal bool HeapPush(TElement element, TPriority priority)
    {
        if (_size == _nodes.Length)
        {
            Grow();
        }

        int index = _size;
        bool becameRoot;
        if (HasNonThrowingComparer)
        {
            becameRoot = FastMoveUpDefaultComparer(index, element, priority);
        }
        else if (typeof(TPriority).IsValueType && _comparer is null)
        {
            becameRoot = MoveUpDefaultComparer(index, element, priority);
        }
        else
        {
            becameRoot = MoveUpCustomComparer(index, element, priority);
        }

        _size = index + 1;
        return becameRoot;
    }

    /// <summary>
    /// Attempts to remove and return the minimum (root) entry, restoring the heap invariant by
    /// moving the last entry into the hole and sifting it down.
    /// </summary>
    /// <param name="element">The popped element, when the heap was non-empty.</param>
    /// <param name="priority">The popped (minimum) priority, when the heap was non-empty.</param>
    /// <returns><see langword="true"/> if an entry was popped; <see langword="false"/> if empty.</returns>
    /// <param name="trackRoot">Whether to precompute publication changes.</param>
    /// <remarks>
    /// The clear of the vacated slot at <c>last</c> runs only when the entry type contains
    /// references, so that the slot does not keep them reachable. Entries of value types only skip
    /// the write.
    /// </remarks>
    internal bool TryHeapPop(out TElement element, out TPriority priority, bool trackRoot = false)
    {
        if (_size == 0)
        {
            element = default!;
            priority = default!;
            return false;
        }

        (TElement Element, TPriority Priority)[] nodes = _nodes;
        (element, priority) = nodes[0];

        int last = _size - 1;

        if (last > 0)
        {
            (TElement Element, TPriority Priority) moving = nodes[last];

            if (HasNonThrowingComparer)
            {
                FastMoveDownDefaultComparer(0, moving.Element, moving.Priority, last);
                if (trackRoot) _rootChanged = CompareEffective(nodes[0].Priority, priority) != 0;
            }
            else if (typeof(TPriority).IsValueType && _comparer is null)
            {
                MoveDownDefaultComparer(0, moving.Element, moving.Priority, last, trackRoot);
            }
            else
            {
                MoveDownCustomComparer(0, moving.Element, moving.Priority, last, trackRoot);
            }
        }

        if (RuntimeHelpers.IsReferenceOrContainsReferences<(TElement, TPriority)>())
        {
            WriteNode(last, default);
        }

        _size = last;
        return true;
    }

    /// <summary>Reads the minimum (root) entry without removing it.</summary>
    /// <param name="element">The root element, when the heap is non-empty.</param>
    /// <param name="priority">The root (minimum) priority, when the heap is non-empty.</param>
    /// <returns><see langword="true"/> if a root exists; <see langword="false"/> if empty.</returns>
    internal bool TryHeapPeekRoot(out TElement element, out TPriority priority)
    {
        if (_size == 0)
        {
            element = default!;
            priority = default!;
            return false;
        }

        (element, priority) = _nodes[0];
        return true;
    }

    /// <summary>
    /// Peeks this sub-queue's LIVE minimum entry without removing it: <c>D.front()</c> (the buffered
    /// resident minimum, which is also the published top) when buffering is enabled, otherwise the
    /// arity-4 heap root. Used by the best-observed revalidation path
    /// (<see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryPeekBestObserved"/> and
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryDequeueBestObserved"/>), which re-reads the
    /// winning sub-queue's live minimum under the held lock before accepting or popping it.
    /// </summary>
    /// <remarks>
    /// Called only under <see cref="SyncLock"/> by the best-observed path, but like
    /// <see cref="TryHeapPeekRoot"/> it does not itself assert the lock. The
    /// <c>_bufferCapacity == 0</c> branch delegates verbatim to <see cref="TryHeapPeekRoot"/>, keeping
    /// the unbuffered (default) path bit-exact with the pre-feature behavior; the buffered branch reads
    /// <c>D.front()</c>, the true live minimum, because on the buffered path the heap root is a LARGER
    /// key (the smallest entries were refilled into <c>D</c>) and the heap can even be empty while
    /// <c>D</c> is non-empty — a heap-only peek would mis-revalidate or wrongly report empty. On the
    /// buffered branch, <c>D</c> is empty exactly when the whole sub-queue is empty, so
    /// <c>_deletionCount == 0</c> is a complete emptiness test.
    /// </remarks>
    /// <param name="element">The live minimum element, when the sub-queue is non-empty.</param>
    /// <param name="priority">The live minimum priority, when the sub-queue is non-empty.</param>
    /// <returns><see langword="true"/> if a live minimum exists; <see langword="false"/> if empty.</returns>
    internal bool TryPeekLiveMin(out TElement element, out TPriority priority)
    {
        if (_bufferCapacity > 0)
        {
            if (_deletionCount == 0)
            {
                element = default!;
                priority = default!;
                return false;
            }

            (element, priority) = DeletionSpan(_deletionCount)[0];
            return true;
        }

        return TryHeapPeekRoot(out element, out priority);
    }

    /// <summary>
    /// Grows the backing store by doubling (from <see cref="InitialCapacity"/> on the first
    /// growth), clamped to <see cref="Array.MaxLength"/> with guaranteed forward progress,
    /// the <see cref="PriorityQueue{TElement, TPriority}"/> <c>Grow</c> precedent.
    /// </summary>
    /// <remarks>
    /// The capacity arithmetic (the doubling, the <see cref="Array.MaxLength"/> clamp, the
    /// forward-progress floor, and the "cannot grow further" throw at the exact limit) is in the pure
    /// static <see cref="ComputeGrownCapacity"/>. Tests can then reach the
    /// <see cref="Array.MaxLength"/> boundary through <see cref="ComputeGrownCapacityForTest"/>
    /// without a multi-gigabyte allocation. A real push cannot reach those boundary clamps.
    /// </remarks>
    private void Grow()
    {
        int newCapacity = ComputeGrownCapacity(_nodes.Length);
        Array.Resize(ref _nodes, newCapacity);
    }

    /// <summary>
    /// The first allocation size used when the backing store is empty. Factored out of
    /// <see cref="ComputeGrownCapacity"/> so the growth arithmetic reads identically to the original.
    /// </summary>
    private const int GrowInitialCapacity = InitialCapacity;

    /// <summary>
    /// Computes the next backing-store capacity from the current length: doubles it, clamps to
    /// <see cref="Array.MaxLength"/>, applies a forward-progress floor (the first growth jumps to
    /// <see cref="InitialCapacity"/>, later growths add at least four), re-clamps that floor, and
    /// throws when the result cannot exceed the current length (already at <see cref="Array.MaxLength"/>).
    /// A pure function of <paramref name="currentLength"/> with no instance state, so the
    /// <see cref="Array.MaxLength"/> boundary clamps and the "cannot grow further" throw are testable
    /// directly without allocating an array of that size.
    /// </summary>
    /// <param name="currentLength">The current backing-store length (<c>_nodes.Length</c>).</param>
    /// <returns>The new, strictly-larger capacity to resize to.</returns>
    /// <exception cref="InvalidOperationException">
    /// The store is already at <see cref="Array.MaxLength"/> and cannot grow further.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The first clamp of <c>newCapacity</c> lets the heap grow to the maximum possible capacity
    /// before an overflow occurs. Without it, a doubling past 2^30 entries overflows to a negative
    /// value and surfaces as a wrong-typed <see cref="ArgumentOutOfRangeException"/>.
    /// </para>
    /// <para>
    /// The floor <c>minCapacity</c> guarantees forward progress, and the first growth allocates
    /// <see cref="InitialCapacity"/> outright. Near the boundary the floor itself can exceed
    /// <see cref="Array.MaxLength"/>, so it is re-clamped before the <c>Math.Max</c>. Otherwise the
    /// first clamp is undone and the resize throws a wrong-typed exception at the exact limit that
    /// this method exists to handle.
    /// </para>
    /// <para>
    /// When the store is already at <see cref="Array.MaxLength"/> with no room to grow, the method
    /// throws a clear, typed failure instead of a resize to a non-increasing length.
    /// </para>
    /// </remarks>
    private static int ComputeGrownCapacity(int currentLength)
    {
        const int GrowFactor = 2;
        const int MinimumGrow = 4;

        int newCapacity = GrowFactor * currentLength;

        if ((uint)newCapacity > Array.MaxLength)
        {
            newCapacity = Array.MaxLength;
        }

        int minCapacity = currentLength == 0 ? GrowInitialCapacity : currentLength + MinimumGrow;
        if ((uint)minCapacity > Array.MaxLength)
        {
            minCapacity = Array.MaxLength;
        }

        newCapacity = Math.Max(newCapacity, minCapacity);
        if (newCapacity <= currentLength)
        {
            throw new InvalidOperationException("Sub-queue reached its maximum capacity and cannot grow further.");
        }

        return newCapacity;
    }

    /// <summary>
    /// TEST-ONLY: exposes <see cref="ComputeGrownCapacity"/> so the growth arithmetic — including the
    /// <see cref="Array.MaxLength"/> clamp and the "cannot grow further" throw at the boundary — can be
    /// exercised directly with boundary-sized lengths that a real push could never allocate.
    /// </summary>
    /// <param name="currentLength">The hypothetical current backing-store length.</param>
    /// <returns>The new capacity <see cref="ComputeGrownCapacity"/> would resize to.</returns>
    internal static int ComputeGrownCapacityForTest(int currentLength) => ComputeGrownCapacity(currentLength);

    /// <summary>
    /// Hole-based sift-up on the devirtualized default-comparer path: walks the moving entry toward
    /// the root, shifting larger ancestors down into the hole, and places it once at the end.
    /// Mirrors <c>PriorityQueue.MoveUpDefaultComparer</c>.
    /// </summary>
    private bool MoveUpDefaultComparer(int index, TElement element, TPriority priority)
    {
        var nodes = _nodes;
        int target = index;
        while (target > 0)
        {
            int parent = (target - 1) >> 2;
            if (Comparer<TPriority>.Default.Compare(priority, nodes[parent].Priority) >= 0) break;
            target = parent;
        }

        CommitAscent(index, target, element, priority);
        return target == 0;
    }

    /// <summary>Compares the sift path before committing any heap writes.</summary>
    private bool MoveUpCustomComparer(int index, TElement element, TPriority priority)
    {
        var nodes = _nodes;
        var comparer = _comparer!;
        int target = index;
        while (target > 0)
        {
            int parent = (target - 1) >> 2;
            if (comparer.Compare(priority, nodes[parent].Priority) >= 0) break;
            target = parent;
        }

        CommitAscent(index, target, element, priority);
        return target == 0;
    }

    /// <summary>Records the chosen children before committing the sift.</summary>
    /// <remarks>
    /// The <c>path</c> buffer holds 16 entries, because an Int32-indexed quaternary heap has at most
    /// 16 levels.
    /// </remarks>
    private void MoveDownDefaultComparer(int index, TElement element, TPriority priority, int count, bool trackRoot)
    {
        var nodes = _nodes;
        Span<int> path = stackalloc int[16];
        int length = 0;
        int start = index;
        int lastParent = (count - 2) >> 2;
        while (index <= lastParent)
        {
            int firstChild = (index << 2) + 1;
            int minChild = firstChild;
            TPriority minPriority = nodes[firstChild].Priority;
            int lastChild = Math.Min(firstChild + 3, count - 1);
            for (int child = firstChild + 1; child <= lastChild; child++)
            {
                TPriority candidate = nodes[child].Priority;
                if (Comparer<TPriority>.Default.Compare(candidate, minPriority) >= 0) continue;
                minChild = child;
                minPriority = candidate;
            }

            if (Comparer<TPriority>.Default.Compare(minPriority, priority) >= 0) break;
            path[length++] = minChild;
            index = minChild;
        }

        if (trackRoot)
        {
            TPriority nextRoot = length == 0 ? priority : nodes[path[0]].Priority;
            _rootChanged = Comparer<TPriority>.Default.Compare(nextRoot, nodes[start].Priority) != 0;
        }

        index = start;
        if (_journalActive)
        {
            for (int i = 0; i < length; i++)
            {
                WriteNode(index, nodes[path[i]]);
                index = path[i];
            }

            WriteNode(index, (element, priority));
        }
        else
        {
            for (int i = 0; i < length; i++)
            {
                nodes[index] = nodes[path[i]];
                index = path[i];
            }

            nodes[index] = (element, priority);
        }
    }

    /// <summary>Records the chosen children before committing the sift.</summary>
    /// <remarks>
    /// The <c>path</c> buffer holds 16 entries, because an Int32-indexed quaternary heap has at most
    /// 16 levels.
    /// </remarks>
    private void MoveDownCustomComparer(int index, TElement element, TPriority priority, int count, bool trackRoot)
    {
        var nodes = _nodes;
        Span<int> path = stackalloc int[16];
        int length = 0;
        int start = index;
        int lastParent = (count - 2) >> 2;
        while (index <= lastParent)
        {
            int firstChild = (index << 2) + 1;
            int minChild = firstChild;
            TPriority minPriority = nodes[firstChild].Priority;
            int lastChild = Math.Min(firstChild + 3, count - 1);
            for (int child = firstChild + 1; child <= lastChild; child++)
            {
                TPriority candidate = nodes[child].Priority;
                if (_comparer!.Compare(candidate, minPriority) >= 0) continue;
                minChild = child;
                minPriority = candidate;
            }

            if (_comparer!.Compare(minPriority, priority) >= 0) break;
            path[length++] = minChild;
            index = minChild;
        }

        if (trackRoot)
        {
            TPriority nextRoot = length == 0 ? priority : nodes[path[0]].Priority;
            _rootChanged = _comparer!.Compare(nextRoot, nodes[start].Priority) != 0;
        }

        index = start;
        if (_journalActive)
        {
            for (int i = 0; i < length; i++)
            {
                WriteNode(index, nodes[path[i]]);
                index = path[i];
            }

            WriteNode(index, (element, priority));
        }
        else
        {
            for (int i = 0; i < length; i++)
            {
                nodes[index] = nodes[path[i]];
                index = path[i];
            }

            nodes[index] = (element, priority);
        }
    }

    /// <summary>Commits a compared ascent, testing transaction state once outside the copy loop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CommitAscent(int index, int target, TElement element, TPriority priority)
    {
        var nodes = _nodes;
        if (_journalActive)
        {
            while (index > target)
            {
                int parent = (index - 1) >> 2;
                WriteNode(index, nodes[parent]);
                index = parent;
            }

            WriteNode(index, (element, priority));
        }
        else
        {
            while (index > target)
            {
                int parent = (index - 1) >> 2;
                nodes[index] = nodes[parent];
                index = parent;
            }

            nodes[index] = (element, priority);
        }
    }

    /// <summary>
    /// Attempts to acquire the sub-queue lock and push an entry, maintaining the published top
    /// and the striped count. Never blocks: a contended lock yields an immediate
    /// <see langword="false"/> so the caller can resample another sub-queue.
    /// </summary>
    /// <param name="element">The element to store.</param>
    /// <param name="priority">The priority that orders the entry.</param>
    /// <returns>
    /// <see langword="true"/> when the entry was pushed; <see langword="false"/> when the lock
    /// was contended and nothing was done.
    /// </returns>
    /// <remarks>
    /// Buffering is opt-in (<c>_bufferCapacity &gt; 0</c>). When the insertion buffer <c>I</c> is
    /// full, one push causes several heap mutations, so an unknown comparer needs the rollback of
    /// <see cref="BufferedTransaction"/>. Built-in numeric comparisons cannot throw. The
    /// <see cref="HasNonThrowingComparer"/> test comes first, so those comparers skip the field reads
    /// that trigger the transaction.
    /// </remarks>
    internal bool TryLockedPush(TElement element, TPriority priority)
    {
        if (!SyncLock.TryEnter())
        {
            return false;
        }

        try
        {
            if (_bufferCapacity > 0)
            {
                if (!HasNonThrowingComparer && _insertionCount == _bufferCapacity)
                {
                    BufferedTransaction(element, priority, pop: false, out _, out _);
                }
                else
                {
                    BufferedPush(element, priority);
                }
            }
            else
            {
                UnbufferedPush(element, priority);
            }

            return true;
        }
        finally
        {
            SyncLock.Exit();
        }
    }

    /// <summary>
    /// The unbuffered (<c>bufferCapacity == 0</c>) push: heap-push with comparer exception safety,
    /// republish only on a root change, set occupancy on the empty→non-empty crossing, write the count.
    /// Assumes <see cref="SyncLock"/> is held.
    /// </summary>
    /// <param name="element">The element to store.</param>
    /// <param name="priority">The priority that orders the entry.</param>
    /// <remarks>
    /// <para>
    /// When the sift reaches index zero, that alone proves a strict root change. The method reuses
    /// that result from <see cref="HeapPush"/> as <c>publish</c> and does not call the comparer again.
    /// It republishes only when the minimum changed: on the first entry, or when the new entry is
    /// strictly smaller than the previous root. An equal-priority push leaves the published value
    /// correct.
    /// </para>
    /// <para>
    /// The occupancy transition is boundary-only. The empty→non-empty crossing is exactly
    /// <c>wasEmpty</c>, so the method sets this sub-queue's bit together with the seqlock publish,
    /// under the held lock. A push onto a populated sub-queue leaves <c>wasEmpty</c> false and never
    /// touches the bitmask. This keeps the bitmask dormant on the dense hot path.
    /// </para>
    /// </remarks>
    private void UnbufferedPush(TElement element, TPriority priority)
    {
        bool wasEmpty = _size == 0;
        bool publish = HeapPush(element, priority);

        if (publish)
        {
            PublishTop(priority, empty: false);
        }

        if (wasEmpty)
        {
            SetOccupancyBit();
        }

        Volatile.Write(ref _header.Count, _size);
    }

    /// <summary>
    /// Attempts to acquire the sub-queue lock and pop the minimum entry, maintaining the
    /// published top and the striped count. Never blocks on a contended lock.
    /// </summary>
    /// <param name="element">The removed element, on <see cref="SubQueuePopStatus.Success"/>.</param>
    /// <param name="priority">The removed priority, on <see cref="SubQueuePopStatus.Success"/>.</param>
    /// <returns>
    /// <see cref="SubQueuePopStatus.Success"/> with the former root;
    /// <see cref="SubQueuePopStatus.Empty"/> when the lock was acquired but the heap is empty
    /// (counts toward a caller's empty-verification pass); or
    /// <see cref="SubQueuePopStatus.Contended"/> when the lock was held elsewhere and nothing
    /// was observed (the caller resamples; progress may depend on the lock holder resuming).
    /// </returns>
    internal SubQueuePopStatus TryLockedPop(out TElement element, out TPriority priority)
    {
        if (!SyncLock.TryEnter())
        {
            element = default!;
            priority = default!;
            return SubQueuePopStatus.Contended;
        }

        try
        {
            return PopHeldRoot(out element, out priority);
        }
        finally
        {
            SyncLock.Exit();
        }
    }

    /// <summary>
    /// Pops the minimum entry assuming <see cref="SyncLock"/> is <i>already held by the caller</i>,
    /// maintaining the published top and the striped count. The caller owns acquiring and releasing
    /// the lock; this lets a caller that revalidated the live root under the lock pop that exact root
    /// without an intervening unlock window in which another thread could swap it (the best-observed
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryDequeueBestObserved"/> path relies on this).
    /// </summary>
    /// <param name="element">The removed element, on <see cref="SubQueuePopStatus.Success"/>.</param>
    /// <param name="priority">The removed priority, on <see cref="SubQueuePopStatus.Success"/>.</param>
    /// <returns>
    /// <see cref="SubQueuePopStatus.Success"/> with the former root, or
    /// <see cref="SubQueuePopStatus.Empty"/> when the heap is empty. Never returns
    /// <see cref="SubQueuePopStatus.Contended"/>: the caller already holds the lock.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A buffered removal serves <c>D.front()</c> and refills <c>D</c> from the heap when <c>D</c>
    /// empties. An unknown comparer needs rollback across that compound refill, so the method then
    /// goes through <see cref="BufferedTransaction"/>.
    /// </para>
    /// <para>
    /// With at most one remaining entry, the flush and the refill cannot compare: a push into an
    /// empty heap and a pop of its sole entry invoke no user code. That case keeps the identical
    /// refill path, but it does not copy both buffers or open a journal. <c>_header.Count</c> is
    /// exact under this lock. With <c>D</c> holding one item, <c>Count &gt; 2</c> means that the
    /// refill has at least two remaining entries and can invoke the comparer.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SubQueuePopStatus PopHeldRoot(out TElement element, out TPriority priority)
    {
        Debug.Assert(SyncLock.IsHeldByCurrentThread, "PopHeldRoot requires the sub-queue lock held by the caller.");

        if (_bufferCapacity == 0) return UnbufferedPopHeldRoot(out element, out priority);
        if (!HasNonThrowingComparer && _header.Count > 2 && _deletionCount == 1)
            return BufferedTransaction(default!, default!, pop: true, out element, out priority);
        return BufferedPopHeldRoot(out element, out priority);
    }

    /// <summary>
    /// The unbuffered (<c>bufferCapacity == 0</c>) pop: heap-pop with comparer exception safety,
    /// publish empty + clear occupancy on a drain, else republish the new root only on a change, write
    /// the count. Assumes <see cref="SyncLock"/> is held.
    /// </summary>
    /// <param name="element">The removed element, on <see cref="SubQueuePopStatus.Success"/>.</param>
    /// <param name="priority">The removed priority, on <see cref="SubQueuePopStatus.Success"/>.</param>
    /// <returns><see cref="SubQueuePopStatus.Success"/> with the former root, or <see cref="SubQueuePopStatus.Empty"/>.</returns>
    /// <remarks>
    /// <para>
    /// The occupancy transition is boundary-only. The non-empty→empty crossing is exactly
    /// <c>_size == 0</c> after the pop, so the method clears this sub-queue's bit together with the
    /// empty publish, under the held lock. A pop that leaves entries behind takes the else-branch and
    /// never touches the bitmask. <see cref="PopHeldRoot"/> is the single drain funnel, so
    /// <see cref="TryLockedPop"/> inherits this clear.
    /// </para>
    /// <para>
    /// The method republishes only when the priority of the exposed root (<c>newRoot</c>) differs
    /// from the popped one. A duplicate minimum leaves the published priority value correct.
    /// </para>
    /// </remarks>
    private SubQueuePopStatus UnbufferedPopHeldRoot(out TElement element, out TPriority priority)
    {
        if (!TryHeapPop(out element, out priority, trackRoot: !HasNonThrowingComparer))
        {
            return SubQueuePopStatus.Empty;
        }

        if (_size == 0)
        {
            PublishTop(priority, empty: true);

            ClearOccupancyBit();
        }
        else
        {
            TPriority newRoot = _nodes[0].Priority;
            if (HasNonThrowingComparer ? CompareEffective(newRoot, priority) != 0 : _rootChanged)
            {
                PublishTop(newRoot, empty: false);
            }
        }

        Volatile.Write(ref _header.Count, _size);
        return SubQueuePopStatus.Success;
    }

    // =====================================================================================
    // Buffered push/pop (active only when _bufferCapacity > 0). All methods here
    // assume SyncLock is held. The arity-4 heap is touched only on an I-flush or a D-refill,
    // removing the deep-heap cache-line walk from the hot path; D.front() is the published top,
    // the occupancy bit and EmptyFlag track D's 0↔non-0 boundary, and Count is I+D+heap.
    // =====================================================================================

    /// <summary>
    /// The buffered push. Routes <c>(element, priority)</c> by the reference
    /// <c>BufferedPQ</c> rules: a small key (<c>v ≤ max(D)</c>) sorted-inserts into the deletion buffer
    /// <c>D</c> (evicting <c>max(D)</c> into <c>I</c>/heap when <c>D</c> is full); the first key into an
    /// otherwise-empty structure seeds <c>D</c> directly; otherwise the key appends to the insertion
    /// buffer <c>I</c>, which flushes wholesale into the heap when it fills. Republishes
    /// <c>D.front()</c>, drives the occupancy bit on the <c>D</c> boundary, and writes the
    /// <c>I+D+heap</c> count. Assumes <see cref="SyncLock"/> is held.
    /// </summary>
    /// <param name="element">The element to store.</param>
    /// <param name="priority">The priority that orders the entry.</param>
    /// <remarks>
    /// <para>
    /// A small key belongs among the resident minima, so <see cref="SortedInsertIntoDeletion"/> puts
    /// it into <c>D</c>. If <c>D</c> was full, its displaced maximum cascades out to <c>I</c>, and on
    /// to the heap if <c>I</c> is also full.
    /// </para>
    /// <para>
    /// An empty <c>D</c> means that the whole structure is empty (the refill invariant). The seed of
    /// <c>D</c> by <see cref="DirectInsertIntoDeletion"/> makes <c>D.front()</c> the minimum at once
    /// and leaves the heap untouched on the tiny-structure path.
    /// </para>
    /// <para>
    /// A larger key with <c>D</c> non-empty cannot be a resident minimum yet, so it waits in
    /// <c>I</c>. A full <c>I</c> flushes to the heap before this key lands, which keeps <c>I</c>
    /// bounded by the logical capacity <c>C</c>.
    /// </para>
    /// <para>
    /// <c>D.front()</c> is the published minimum, and <c>D</c> is non-empty exactly when the
    /// sub-queue is occupied. A buffered push always leaves the structure non-empty, so the only
    /// boundary that it can cross is empty→non-empty (<c>wasEmpty</c>).
    /// </para>
    /// </remarks>
    private void BufferedPush(TElement element, TPriority priority)
    {
        bool wasEmpty = _deletionCount == 0;

        if (_deletionCount > 0 && CompareEffective(priority, DeletionMax) <= 0)
        {
            SortedInsertIntoDeletion(element, priority);
        }
        else if (wasEmpty)
        {
            Debug.Assert(
                _insertionCount == 0 && _size == 0,
                "Buffered invariant violated: D is empty on a push but I or the heap still hold entries.");
            DirectInsertIntoDeletion(element, priority);
        }
        else
        {
            AppendToInsertion(element, priority);
        }

        RepublishBufferedTop();
        if (wasEmpty)
        {
            SetOccupancyBit();
        }

        WriteBufferedCount();
    }

    /// <summary>
    /// The buffered pop: returns and removes <c>D.front()</c> (the sub-queue minimum). If
    /// that empties <c>D</c> while <c>I</c> or the heap still hold entries, refills <c>D</c> with the
    /// smallest <c>min(C, |heap|)</c> entries (flushing <c>I</c> into the heap first). Republishes the
    /// new <c>D.front()</c> (or empty), drives the occupancy bit on the <c>D</c> boundary, and writes
    /// the <c>I+D+heap</c> count. Assumes <see cref="SyncLock"/> is held.
    /// </summary>
    /// <param name="element">The removed element, on <see cref="SubQueuePopStatus.Success"/>.</param>
    /// <param name="priority">The removed (minimum) priority, on <see cref="SubQueuePopStatus.Success"/>.</param>
    /// <returns><see cref="SubQueuePopStatus.Success"/> with the former front, or <see cref="SubQueuePopStatus.Empty"/>.</returns>
    /// <remarks>
    /// <para>
    /// An empty <c>D</c> means that the whole sub-queue is empty (the invariant), so there is nothing
    /// to pop. The removal of <c>D.front()</c> (slot 0) shifts the rest of <c>D</c> down one slot to
    /// keep the sorted order.
    /// </para>
    /// <para>
    /// For tiny entries without references (at most <c>sizeof(ulong)</c>), a plain forward loop does
    /// the shift and avoids the transition to the native overlapping copy. Larger entries and entries
    /// with GC references keep the bulk copy of the runtime. The forward copy is safe for the overlap.
    /// It does not compare and does not change the refill path.
    /// </para>
    /// <para>
    /// If <c>D</c> is still empty after the refill check, the whole sub-queue is now empty: the
    /// refill found nothing, or there was nothing to refill.
    /// </para>
    /// </remarks>
    private SubQueuePopStatus BufferedPopHeldRoot(out TElement element, out TPriority priority)
    {
        if (_deletionCount == 0)
        {
            Debug.Assert(
                _insertionCount == 0 && _size == 0,
                "Buffered invariant violated: D is empty but I or the heap still hold entries.");
            element = default!;
            priority = default!;
            return SubQueuePopStatus.Empty;
        }

        Span<(TElement Element, TPriority Priority)> d = DeletionSpan(_deletionCount);
        (element, priority) = d[0];
        CountBufferedPopHit();

        int remaining = _deletionCount - 1;
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<(TElement, TPriority)>() &&
            Unsafe.SizeOf<(TElement, TPriority)>() <= sizeof(ulong))
        {
            for (int i = 0; i < remaining; i++) d[i] = d[i + 1];
        }
        else if (remaining > 0)
        {
            d.Slice(1, remaining).CopyTo(d.Slice(0, remaining));
        }

        ClearDeletionSlot(remaining);
        _deletionCount = remaining;

        if (_deletionCount == 0 && (_insertionCount > 0 || _size > 0))
        {
            RefillDeletion();
        }

        if (_deletionCount == 0)
        {
            Debug.Assert(
                _insertionCount == 0 && _size == 0,
                "Buffered invariant violated: D drained to empty but I or the heap still hold entries.");
            PublishTop(priority, empty: true);
            ClearOccupancyBit();
        }
        else
        {
            RepublishBufferedTop();
        }

        WriteBufferedCount();
        return SubQueuePopStatus.Success;
    }

    // Built-in numeric default comparers cannot invoke user code. Preserve their original
    // single-pass sift hot path; arbitrary IComparable implementations use preflight/rollback.
    private bool HasNonThrowingComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _comparer is null &&
            (typeof(TPriority) == typeof(int) || typeof(TPriority) == typeof(long) ||
             typeof(TPriority) == typeof(uint) || typeof(TPriority) == typeof(ulong) ||
             typeof(TPriority) == typeof(short) || typeof(TPriority) == typeof(ushort) ||
             typeof(TPriority) == typeof(byte) || typeof(TPriority) == typeof(sbyte) ||
             typeof(TPriority) == typeof(float) || typeof(TPriority) == typeof(double) ||
             typeof(TPriority) == typeof(decimal) || typeof(TPriority) == typeof(char));
    }

    private bool FastMoveUpDefaultComparer(int index, TElement element, TPriority priority)
    {
        (TElement Element, TPriority Priority)[] nodes = _nodes;

        while (index > 0)
        {
            int parent = (index - 1) >> 2;

            if (Comparer<TPriority>.Default.Compare(priority, nodes[parent].Priority) >= 0)
            {
                break;
            }

            nodes[index] = nodes[parent];
            index = parent;
        }

        nodes[index] = (element, priority);
        return index == 0;
    }

    private void FastMoveDownDefaultComparer(int index, TElement element, TPriority priority, int count)
    {
        (TElement Element, TPriority Priority)[] nodes = _nodes;

        while (true)
        {
            int firstChild = (index << 2) + 1;
            if (firstChild >= count)
            {
                break;
            }

            int minChild = firstChild;
            TPriority minPriority = nodes[firstChild].Priority;
            int lastChild = Math.Min(firstChild + 3, count - 1);

            for (int child = firstChild + 1; child <= lastChild; child++)
            {
                TPriority candidate = nodes[child].Priority;
                if (Comparer<TPriority>.Default.Compare(candidate, minPriority) >= 0)
                {
                    continue;
                }

                minChild = child;
                minPriority = candidate;
            }

            if (Comparer<TPriority>.Default.Compare(minPriority, priority) >= 0)
            {
                break;
            }

            nodes[index] = nodes[minChild];
            index = minChild;
        }

        nodes[index] = (element, priority);
    }

    private bool _rootChanged;
    private bool _journalActive;
    private int _journalCount;
    private (int Index, (TElement Element, TPriority Priority) Entry)[] _journal = [];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteNode(int index, (TElement Element, TPriority Priority) entry)
    {
        if (_journalActive) RecordOverwrite(index);
        _nodes[index] = entry;
    }

    // Keep allocation and journal bookkeeping out of every inlined heap write.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RecordOverwrite(int index)
    {
        if (_journalCount == _journal.Length)
            Array.Resize(ref _journal, Math.Max(32, _journalCount * 2));
        _journal[_journalCount++] = (index, _nodes[index]);
    }

    /// <summary>Rolls back a compound buffer operation without invoking user code.</summary>
    private SubQueuePopStatus BufferedTransaction(TElement value, TPriority key, bool pop,
        out TElement element, out TPriority priority)
    {
        var insertion = _insertion;
        var deletion = _deletion;
        int insertionCount = _insertionCount, deletionCount = _deletionCount, size = _size;
        _journalActive = true;
        try
        {
            if (pop) return BufferedPopHeldRoot(out element, out priority);
            BufferedPush(value, key);
            element = default!;
            priority = default!;
            return SubQueuePopStatus.Success;
        }
        catch
        {
            for (int i = _journalCount - 1; i >= 0; i--)
                _nodes[_journal[i].Index] = _journal[i].Entry;
            _insertion = insertion;
            _deletion = deletion;
            _insertionCount = insertionCount;
            _deletionCount = deletionCount;
            _size = size;
            throw;
        }
        finally
        {
            _journalActive = false;
            if (RuntimeHelpers.IsReferenceOrContainsReferences<(TElement, TPriority)>())
                Array.Clear(_journal, 0, _journalCount);
            _journalCount = 0;
        }
    }

    /// <summary>
    /// Gets the maximum priority currently in the sorted deletion buffer <c>D</c> (its last slot).
    /// Valid only when <see cref="_deletionCount"/> is positive.
    /// </summary>
    private TPriority DeletionMax => DeletionSpan(_deletionCount)[_deletionCount - 1].Priority;

    /// <summary>Creates a <see cref="Span{T}"/> over the first <paramref name="length"/> slots of the deletion buffer <c>D</c>.</summary>
    /// <param name="length">The logical length to expose.</param>
    /// <returns>A span aliasing <see cref="_deletion"/>'s leading storage.</returns>
    private Span<(TElement Element, TPriority Priority)> DeletionSpan(int length)
        => SubQueueBuffer<TElement, TPriority>.AsSpan(ref _deletion, length);

    /// <summary>Creates a <see cref="Span{T}"/> over the first <paramref name="length"/> slots of the insertion buffer <c>I</c>.</summary>
    /// <param name="length">The logical length to expose.</param>
    /// <returns>A span aliasing <see cref="_insertion"/>'s leading storage.</returns>
    private Span<(TElement Element, TPriority Priority)> InsertionSpan(int length)
        => SubQueueBuffer<TElement, TPriority>.AsSpan(ref _insertion, length);

    /// <summary>
    /// Seeds the empty deletion buffer with a single entry (the otherwise-empty-structure direct-to-D
    /// case). Assumes <c>_deletionCount == 0</c> and <see cref="SyncLock"/> held.
    /// </summary>
    /// <param name="element">The element to store.</param>
    /// <param name="priority">The priority that orders the entry.</param>
    private void DirectInsertIntoDeletion(TElement element, TPriority priority)
    {
        DeletionSpan(1)[0] = (element, priority);
        _deletionCount = 1;
        CountDirectToDeletion();
    }

    /// <summary>
    /// Sorted-inserts a small key (<c>priority ≤ max(D)</c>) into the deletion buffer <c>D</c>,
    /// preserving ascending order via a hole-shift over the span. When <c>D</c> is already full
    /// (<c>_deletionCount == C</c>), its current maximum is first evicted into <c>I</c> (cascading to a
    /// heap flush + heap-push if <c>I</c> is also full) so the new key has room. Assumes
    /// <see cref="SyncLock"/> held.
    /// </summary>
    /// <param name="element">The element to store.</param>
    /// <param name="priority">The priority that orders the entry.</param>
    /// <remarks>
    /// <para>
    /// The method first finds <c>hole</c>, the insert position, with the comparer. If <c>D</c> is
    /// full, it then evicts the maximum of <c>D</c> to make room. The evicted maximum is the largest
    /// resident minimum, and it still outranks everything in <c>I</c> and the heap, relative to the
    /// smaller incoming key.
    /// </para>
    /// <para>
    /// The hole-shift moves the block of larger entries from <c>hole</c> up one slot into the
    /// vacant tail, then drops the new entry into the hole. The <c>CopyTo</c> of the moving block is
    /// barrier-correct.
    /// </para>
    /// </remarks>
    private void SortedInsertIntoDeletion(TElement element, TPriority priority)
    {
        int hole = _deletionCount;
        if (hole == _bufferCapacity) hole--;
        var original = DeletionSpan(_deletionCount);
        if (typeof(TPriority).IsValueType && _comparer is null)
        {
            while (hole > 0 && Comparer<TPriority>.Default.Compare(priority, original[hole - 1].Priority) < 0) hole--;
        }
        else
        {
            var comparer = _comparer!;
            while (hole > 0 && comparer.Compare(priority, original[hole - 1].Priority) < 0) hole--;
        }

        if (_deletionCount == _bufferCapacity)
        {
            (TElement Element, TPriority Priority) evicted = DeletionSpan(_deletionCount)[_deletionCount - 1];
            ClearDeletionSlot(_deletionCount - 1);
            _deletionCount--;
            CountEviction();
            EvictToInsertion(evicted.Element, evicted.Priority);
        }

        Span<(TElement Element, TPriority Priority)> d = DeletionSpan(_deletionCount + 1);
        int shift = _deletionCount - hole;
        if (shift > 0)
        {
            d.Slice(hole, shift).CopyTo(d.Slice(hole + 1, shift));
        }

        d[hole] = (element, priority);
        _deletionCount++;
    }

    /// <summary>
    /// Appends an entry to the insertion buffer <c>I</c>; if <c>I</c> is full it first flushes wholesale
    /// into the heap (so <c>I</c> never exceeds the logical capacity <c>C</c>). Assumes
    /// <see cref="SyncLock"/> held.
    /// </summary>
    /// <param name="element">The element to store.</param>
    /// <param name="priority">The priority that orders the entry.</param>
    private void AppendToInsertion(TElement element, TPriority priority)
    {
        if (_insertionCount == _bufferCapacity)
        {
            FlushInsertion();
        }

        InsertionSpan(_insertionCount + 1)[_insertionCount] = (element, priority);
        _insertionCount++;
    }

    /// <summary>
    /// Routes an evicted <c>max(D)</c> entry into the insertion buffer <c>I</c>; identical to
    /// <see cref="AppendToInsertion"/> but kept distinct for the eviction-cascade reading and the
    /// counter site. Assumes <see cref="SyncLock"/> held.
    /// </summary>
    /// <param name="element">The evicted element.</param>
    /// <param name="priority">The evicted priority.</param>
    private void EvictToInsertion(TElement element, TPriority priority) => AppendToInsertion(element, priority);

    /// <summary>
    /// Flushes the entire insertion buffer <c>I</c> into the arity-4 heap (one heap-push per resident
    /// entry) and resets <c>I</c> to empty. Touched only when <c>I</c> fills or on a refill, so the heap
    /// is amortized to ~once per <c>C</c> ops. Assumes <see cref="SyncLock"/> held.
    /// </summary>
    private void FlushInsertion()
    {
        Span<(TElement Element, TPriority Priority)> i = InsertionSpan(_insertionCount);
        for (int k = 0; k < i.Length; k++)
        {
            HeapPush(i[k].Element, i[k].Priority);
        }

        ClearInsertion(_insertionCount);
        _insertionCount = 0;
        CountFlush();
    }

    /// <summary>
    /// Refills the empty deletion buffer <c>D</c> from the heap: first flushes <c>I</c> into the heap
    /// (so every resident entry is heap-ordered), then pops the smallest <c>min(C, |heap|)</c> entries
    /// into <c>D</c> in ascending order. Called only when <c>D</c> emptied on a pop with <c>I</c> or the
    /// heap still populated. Assumes <see cref="SyncLock"/> held and <c>_deletionCount == 0</c>.
    /// </summary>
    /// <remarks>
    /// The heap pops in ascending order, so the front-to-back writes into <c>d</c> keep <c>D</c> sorted.
    /// </remarks>
    private void RefillDeletion()
    {
        Debug.Assert(_deletionCount == 0, "RefillDeletion requires an empty deletion buffer.");

        if (_insertionCount > 0)
        {
            FlushInsertion();
        }

        int take = Math.Min(_bufferCapacity, _size);
        Span<(TElement Element, TPriority Priority)> d = DeletionSpan(take);
        for (int k = 0; k < take; k++)
        {
            bool popped = TryHeapPop(out TElement e, out TPriority p);
            Debug.Assert(popped, "RefillDeletion popped past the heap size.");
            d[k] = (e, p);
        }

        _deletionCount = take;
        CountRefill();
    }

    /// <summary>
    /// Republishes the seqlock top as the current <c>D.front()</c> with the non-empty flag. Centralizes
    /// the buffered publish tail; called whenever <c>D</c> is non-empty after a mutation. Assumes
    /// <see cref="SyncLock"/> held and <c>_deletionCount &gt; 0</c>.
    /// </summary>
    private void RepublishBufferedTop()
    {
        Debug.Assert(_deletionCount > 0, "RepublishBufferedTop requires a non-empty deletion buffer.");
        PublishTop(DeletionSpan(_deletionCount)[0].Priority, empty: false);
    }

    /// <summary>
    /// Writes the buffered logical count <c>I + D + heap</c> (the reference's
    /// <c>insertion_end_ + deletion_end_ + pq_.size()</c>) into the striped header, the value the
    /// cross-queue <c>Count</c>/<c>IsEmpty</c> sum observes. Assumes <see cref="SyncLock"/> held.
    /// </summary>
    private void WriteBufferedCount()
        => Volatile.Write(ref _header.Count, _insertionCount + _deletionCount + _size);

    /// <summary>
    /// Clears a single vacated deletion-buffer slot, but only for reference-containing tuples (to drop a
    /// dead reference); value-type-only tuples skip the write. The write-barrier-safe slot clear.
    /// </summary>
    /// <param name="index">The slot index to clear.</param>
    private void ClearDeletionSlot(int index)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<(TElement, TPriority)>())
        {
            DeletionSpan(index + 1).Slice(index, 1).Clear();
        }
    }

    /// <summary>
    /// Clears the first <paramref name="length"/> deletion-buffer slots, but only for
    /// reference-containing tuples (to drop dead references); value-type-only tuples skip the write.
    /// Used by <see cref="LockedClear"/> on the buffered path. The write-barrier-safe block clear.
    /// </summary>
    /// <param name="length">The number of leading slots to clear.</param>
    private void ClearDeletionRange(int length)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<(TElement, TPriority)>())
        {
            DeletionSpan(length).Clear();
        }
    }

    /// <summary>
    /// Clears the first <paramref name="length"/> insertion-buffer slots, but only for
    /// reference-containing tuples (to drop dead references); value-type-only tuples skip the write.
    /// The write-barrier-safe block clear.
    /// </summary>
    /// <param name="length">The number of leading slots to clear.</param>
    private void ClearInsertion(int length)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<(TElement, TPriority)>())
        {
            InsertionSpan(length).Clear();
        }
    }

    /// <summary>
    /// Empties this sub-queue under its lock, publishing the empty state and zeroing the striped
    /// count, and returns how many entries were removed so the caller can release that many
    /// bounded-capacity reservations. It uses a blocking <c>lock</c> rather than a <c>TryEnter</c>,
    /// as with <see cref="SnapshotTo"/>: <c>Clear</c> is not a hot path and the critical section is
    /// a bounded array clear.
    /// </summary>
    /// <remarks>
    /// When buffering is active the removed count is the full resident set <c>I + D + heap</c>, so the
    /// bounded-reservation release stays exact; all three are cleared (reference-gated). When buffering
    /// is off, <c>I</c> and <c>D</c> are empty and only the heap is cleared — bit-exact with the
    /// pre-feature behavior.
    /// <para>
    /// The clears are gated: they only release references, and entries of value types only skip
    /// the writes. On the unbuffered path the buffer counts are zero, so
    /// <see cref="ClearInsertion"/> and <see cref="ClearDeletionRange"/> do nothing and only the heap
    /// <c>Array.Clear</c> runs.
    /// </para>
    /// <para>
    /// The method reaches the occupancy clear only when <c>removed &gt; 0</c>, that is, when the
    /// sub-queue was non-empty (the non-empty→empty crossing). So it clears the occupancy bit together
    /// with the empty publish. Because of the <c>removed &lt;= 0</c> early return, a
    /// <c>Clear</c> of an empty sub-queue writes nothing.
    /// </para>
    /// </remarks>
    /// <returns>The number of entries removed from this sub-queue.</returns>
    internal int LockedClear()
    {
        lock (SyncLock)
        {
            int removed = _insertionCount + _deletionCount + _size;

            if (removed <= 0)
            {
                return removed;
            }

            if (RuntimeHelpers.IsReferenceOrContainsReferences<(TElement, TPriority)>())
            {
                Array.Clear(_nodes, 0, _size);
            }

            ClearInsertion(_insertionCount);
            if (_deletionCount > 0)
            {
                ClearDeletionRange(_deletionCount);
            }

            _size = 0;
            _insertionCount = 0;
            _deletionCount = 0;
            PublishTop(default!, empty: true);

            ClearOccupancyBit();

            Volatile.Write(ref _header.Count, 0);

            return removed;
        }
    }

    /// <summary>
    /// Compares two priorities through the dual path: the devirtualized
    /// <see cref="Comparer{T}.Default"/> call when the stored comparer is null (value-type
    /// priorities with default ordering), otherwise the cached comparer field. Used only on
    /// lock-held paths, never by <see cref="TryReadTop"/>. Duplicated at the queue level too: the
    /// BCL keeps dual comparer paths local to each type for JIT constant folding.
    /// </summary>
    /// <param name="x">The left priority.</param>
    /// <param name="y">The right priority.</param>
    /// <returns>The comparison result per <see cref="IComparer{T}.Compare"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CompareEffective(TPriority x, TPriority y)
        => typeof(TPriority).IsValueType && _comparer is null
            ? Comparer<TPriority>.Default.Compare(x, y)
            : _comparer!.Compare(x, y);

    /// <summary>
    /// Sets this sub-queue's bit in the shared occupancy bitmask on an empty→non-empty crossing.
    /// Must be called with <see cref="SyncLock"/> held. The write is
    /// <see cref="Interlocked.Or(ref ulong, ulong)"/> rather than a plain store because distinct
    /// sub-queues share a 64-bit word under <i>different</i> per-stripe locks; an atomic OR is the
    /// only way two neighbours can flip their bits in the same word without losing an update.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetOccupancyBit()
    {
        Debug.Assert(SyncLock.IsHeldByCurrentThread, "SetOccupancyBit requires the sub-queue lock.");
        Interlocked.Or(ref _occupancy[_index >> 6], 1UL << (_index & 63));
        CountOccupancyWrite();
    }

    /// <summary>
    /// Clears this sub-queue's bit in the shared occupancy bitmask on a non-empty→empty crossing.
    /// Must be called with <see cref="SyncLock"/> held. Uses
    /// <see cref="Interlocked.And(ref ulong, ulong)"/> with the complemented bit mask for the same
    /// word-sharing reason as <see cref="SetOccupancyBit"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearOccupancyBit()
    {
        Debug.Assert(SyncLock.IsHeldByCurrentThread, "ClearOccupancyBit requires the sub-queue lock.");
        Interlocked.And(ref _occupancy[_index >> 6], ~(1UL << (_index & 63)));
        CountOccupancyWrite();
    }

    /// <summary>
    /// TEST-ONLY instrumentation hook: counts one occupancy-bit write (set or clear). Always called
    /// under <see cref="SyncLock"/>, so the plain increment is race-free, and it runs <i>only on a
    /// boundary crossing</i> — never on the dense hot path where the bitmask is already dormant —
    /// touching only this sub-queue's own cold field, never the shared hot bitmask word. The
    /// increment is compiled in only behind <c>BIFROST_TEST_HOOKS</c> (defined for the
    /// <c>InternalsVisibleTo</c> test builds, stripped from the shipped package by the publish
    /// workflow); off that path the method body is empty and the JIT inlines it away. See
    /// Bifrost.Concurrency.csproj.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountOccupancyWrite()
    {
#if BIFROST_TEST_HOOKS
        _debugOccupancyWriteCount++;
#endif
    }

    #region Buffered counters

    // The buffered-path instrumentation mirrors CountOccupancyWrite exactly: each method is always
    // called under SyncLock (so the plain increment is race-free) but its body compiles in only behind
    // BIFROST_TEST_HOOKS — stripped from the shipped package by `-p:BifrostTestHooks=false` (the JIT
    // inlines the empty body away). The fields and …ForTest getters stay ungated so the test project
    // still compiles against the stripped library.

    /// <summary>TEST-ONLY hook: counts one insertion-buffer flush. Gated to BIFROST_TEST_HOOKS.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountFlush()
    {
#if BIFROST_TEST_HOOKS
        _debugBufferFlushCount++;
#endif
    }

    /// <summary>TEST-ONLY hook: counts one deletion-buffer refill. Gated to BIFROST_TEST_HOOKS.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountRefill()
    {
#if BIFROST_TEST_HOOKS
        _debugBufferRefillCount++;
#endif
    }

    /// <summary>TEST-ONLY hook: counts one buffered pop served from <c>D.front()</c>. Gated to BIFROST_TEST_HOOKS.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountBufferedPopHit()
    {
#if BIFROST_TEST_HOOKS
        _debugBufferPopHitCount++;
#endif
    }

    /// <summary>TEST-ONLY hook: counts one direct-to-<c>D</c> seed. Gated to BIFROST_TEST_HOOKS.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountDirectToDeletion()
    {
#if BIFROST_TEST_HOOKS
        _debugBufferDirectToDeletionCount++;
#endif
    }

    /// <summary>TEST-ONLY hook: counts one <c>max(D)</c> eviction cascade. Gated to BIFROST_TEST_HOOKS.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountEviction()
    {
#if BIFROST_TEST_HOOKS
        _debugBufferEvictionCount++;
#endif
    }

    #endregion

    /// <summary>
    /// Copies this sub-queue's entries under its lock into <paramref name="buffer"/>. ToArray and
    /// enumeration support: a brief per-queue lock, copied one sub-queue at a time with no
    /// global freeze and no cross-queue consistency claim. It uses a blocking <c>lock</c> rather
    /// than a <c>TryEnter</c> here: enumeration is not a hot path, the design specifies "taking each
    /// lock briefly", and the critical section is a pure array copy.
    /// </summary>
    /// <remarks>
    /// When buffering is active the resident set spans the insertion buffer <c>I</c>, the sorted
    /// deletion buffer <c>D</c>, and the heap; all three are copied so the unordered collection surface
    /// (<c>ToArray</c>/enumeration) reflects every resident element. When buffering is off, <c>I</c> and
    /// <c>D</c> are empty, so only the heap copy runs — bit-exact with the pre-feature behavior.
    /// </remarks>
    /// <param name="buffer">The destination list that receives this sub-queue's live entries.</param>
    internal void SnapshotTo(List<(TElement Element, TPriority Priority)> buffer)
    {
        lock (SyncLock)
        {
            for (int i = 0; i < _insertionCount; i++)
            {
                buffer.Add(InsertionSpan(_insertionCount)[i]);
            }

            for (int i = 0; i < _deletionCount; i++)
            {
                buffer.Add(DeletionSpan(_deletionCount)[i]);
            }

            for (int i = 0; i < _size; i++)
            {
                buffer.Add(_nodes[i]);
            }
        }
    }
}
