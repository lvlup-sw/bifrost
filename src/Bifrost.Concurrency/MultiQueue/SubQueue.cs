// =============================================================================
// <copyright file="SubQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry/Concurrency/MultiQueue/SubQueue.cs)

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Bifrost.Concurrency.MultiQueue;

/// <summary>
/// A single MultiQueue sub-queue. This type owns the sub-queue's writer lock and the cached-top
/// seqlock (DR-4), and the sequential arity-4 implicit min-heap with dual devirtualized comparer
/// paths (DR-5/DR-6). The heap operations are sequential; composing them under the lock with a
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
    /// The comparer used by heap ordering (DR-5/DR-6). The seqlock paths in this type never
    /// invoke it: the cached top is copied raw and emptiness is a flag word.
    /// </summary>
    private readonly IComparer<TPriority>? _comparer;

    /// <summary>The cached top priority, isolated on its own cache line (see <see cref="PaddedTopSlot{TPriority}"/>).</summary>
    private readonly PaddedTopSlot<TPriority> _cachedTop;

    /// <summary>The padded hot fields: striped count, seqlock version, empty flag (see <see cref="SubQueueHeader"/>).</summary>
    private SubQueueHeader _header;

    /// <summary>
    /// The implicit arity-4 min-heap storage (DR-5): an inline array of <c>(element, priority)</c>
    /// entries laid out exactly like <see cref="PriorityQueue{TElement, TPriority}"/> (which also
    /// names this array <c>_nodes</c>), where the children of node <c>i</c> live at
    /// <c>4i+1 .. 4i+4</c> and its parent at <c>(i-1) &gt;&gt; 2</c>. Starts empty and is allocated
    /// at <see cref="InitialCapacity"/> on the first push.
    /// </summary>
    private (TElement Element, TPriority Priority)[] _nodes;

    /// <summary>The number of live entries in <see cref="_nodes"/> (the heap occupies <c>[0, _size)</c>).</summary>
    private int _size;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubQueue{TElement, TPriority}"/> class.
    /// The initial state is a valid, readable, <i>empty</i> publication: version 0 (even) with
    /// the empty flag set, so a reader that samples a brand-new sub-queue gets a stable
    /// <c>(empty: true)</c> snapshot without any writer having run.
    /// </summary>
    /// <param name="comparer">
    /// The priority comparer retained for the heap tasks; <see langword="null"/> selects the
    /// devirtualized <see cref="Comparer{T}.Default"/> path at the queue level (DR-6).
    /// </param>
    internal SubQueue(IComparer<TPriority>? comparer)
    {
        // DR-6 comparer normalization, shared with the queue shell (see
        // PriorityComparerHelpers.InitializeComparer): a stored null selects the devirtualized
        // Comparer<TPriority>.Default path in the hot heap methods.
        _comparer = PriorityComparerHelpers.InitializeComparer(comparer);

        _nodes = [];

        // MUST invoke the explicit parameterless constructor: it allocates the padded backing
        // array. "Simplifying" this to default/default(T) (e.g. to satisfy SA1129) leaves the
        // backing array null and the seqlock NREs on first publish.
#pragma warning disable SA1129
        _cachedTop = new PaddedTopSlot<TPriority>();
#pragma warning restore SA1129
        _header.EmptyFlag = 1; // TopVersion defaults to 0 (even), i.e. "stable".
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
    /// path (DR-6). True exactly when the stored comparer is null, which the constructor
    /// establishes only for value-type priorities whose effective comparer is
    /// <see cref="Comparer{T}.Default"/>. Exposed for the comparer-dispatch tests.
    /// </summary>
    internal bool UsesDefaultComparerPath => _comparer is null;

    /// <summary>
    /// Gets this sub-queue's striped element count via a volatile read. Cross-queue consumers
    /// (DR-12 <c>Count</c>/<c>IsEmpty</c>) sum or short-circuit over these snapshots.
    /// </summary>
    internal int VolatileCount => Volatile.Read(ref _header.Count);

    /// <summary>Gets the number of entries currently in the heap.</summary>
    internal int HeapSize => _size;

    /// <summary>
    /// TEST-ONLY: reads the current seqlock version so tests can assert when a mutation did
    /// (or deliberately did not) republish the cached top.
    /// </summary>
    internal uint DebugTopVersionForTest => Volatile.Read(ref _header.TopVersion);

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
    /// </remarks>
    internal void PublishTop(in TPriority top, bool empty)
    {
        Debug.Assert(SyncLock.IsHeldByCurrentThread, "PublishTop requires the sub-queue lock.");

        // Single writer (lock-held): a plain read of the version cannot race another writer.
        uint version = _header.TopVersion;

        // 1. Stamp odd: announce "writer in progress".
        Volatile.Write(ref _header.TopVersion, version + 1u);

        // 2. Release fence for the *following* writes: the data writes below cannot become
        //    observable before the odd stamp above (Volatile.Write alone orders only PRIOR
        //    accesses; without this fence arm64 may publish data ahead of the odd stamp).
        Volatile.WriteBarrier();

        if (!empty)
        {
            _cachedTop.Set(top);
        }
        else if (RuntimeHelpers.IsReferenceOrContainsReferences<TPriority>())
        {
            // GC hygiene only; emptiness is signalled by the flag, never by the slot value.
            _cachedTop.Set(default!);
        }

        _header.EmptyFlag = empty ? 1 : 0;

        // 3. Stamp even (release): all data writes above are observable no later than this stamp.
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
    /// even version, no writer's window overlapped the copy, so the copy is untorn.
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
                // Plain copies: candidate may be torn if a writer overlaps; the version
                // re-check below discards exactly those cases.
                TPriority candidate = _cachedTop.Get();
                int emptyFlag = _header.EmptyFlag;

                // Acquire fence: the copies above cannot be deferred past the re-check below.
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
    /// TEST-ONLY: forces the seqlock version odd, simulating a writer that never completes, so
    /// tests can pin the bounded-retry "unknown" behaviour of <see cref="TryReadTop"/>.
    /// </summary>
    internal void DebugForceOddVersionForTest()
        => Volatile.Write(ref _header.TopVersion, _header.TopVersion | 1u);

    // ---- Sequential arity-4 heap (DR-5/DR-6) ----
    //
    // Storage is an implicit arity-4 min-heap over an inline (element, priority)[], the exact
    // PriorityQueue<TElement, TPriority> layout. Arity 4 (rather than binary) makes the tree
    // shallower: a pop-dominated workload is sift-down dominated, and each level traversed costs a
    // potential cache miss, so fewer-but-wider levels touch fewer cache lines than a deeper binary
    // tree of the same count. Sifting is hole-based: instead of swapping pairs (two writes each),
    // the moving entry is held in a local while entries are shifted into the vacated hole, and the
    // moving entry is placed once when its final position is found.
    //
    // Comparer dispatch is devirtualized per DR-6: hot methods branch on the JIT-constant
    // `typeof(TPriority).IsValueType && _comparer is null` and call a *DefaultComparer variant
    // (Comparer<TPriority>.Default.Compare at the call site, inlined to an intrinsic for
    // int/long/etc.) or a *CustomComparer variant using the cached comparer field: one comparer
    // call per compare, no further indirection. The method names MoveUp*/MoveDown* mirror the BCL
    // PriorityQueue sift methods. These methods are SEQUENTIAL: they take no lock and publish no
    // top; the locked composition below pairs them with PublishTop under SyncLock.

    /// <summary>
    /// Pushes an <c>(element, priority)</c> entry onto the heap, growing the backing store when
    /// full, then sifting the new entry up to restore the arity-4 min-heap invariant.
    /// </summary>
    /// <param name="element">The element to store.</param>
    /// <param name="priority">The priority that orders the entry.</param>
    internal void HeapPush(TElement element, TPriority priority)
    {
        if (_size == _nodes.Length)
        {
            Grow();
        }

        int index = _size;
        _size = index + 1;

        if (typeof(TPriority).IsValueType && _comparer is null)
        {
            MoveUpDefaultComparer(index, element, priority);
        }
        else
        {
            MoveUpCustomComparer(index, element, priority);
        }
    }

    /// <summary>
    /// Attempts to remove and return the minimum (root) entry, restoring the heap invariant by
    /// moving the last entry into the hole and sifting it down.
    /// </summary>
    /// <param name="element">The popped element, when the heap was non-empty.</param>
    /// <param name="priority">The popped (minimum) priority, when the heap was non-empty.</param>
    /// <returns><see langword="true"/> if an entry was popped; <see langword="false"/> if empty.</returns>
    internal bool TryHeapPop(out TElement element, out TPriority priority)
    {
        if (_size == 0)
        {
            element = default!;
            priority = default!;
            return false;
        }

        (TElement Element, TPriority Priority)[] nodes = _nodes;
        (element, priority) = nodes[0];

        int last = --_size;

        if (last > 0)
        {
            // Move the last entry into the root hole, then sift it down to its place.
            (TElement Element, TPriority Priority) moving = nodes[last];

            if (typeof(TPriority).IsValueType && _comparer is null)
            {
                MoveDownDefaultComparer(0, moving.Element, moving.Priority);
            }
            else
            {
                MoveDownCustomComparer(0, moving.Element, moving.Priority);
            }
        }

        // Gated slot clear: only release references; value-type-only entries skip the write.
        if (RuntimeHelpers.IsReferenceOrContainsReferences<(TElement, TPriority)>())
        {
            nodes[last] = default;
        }

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
    /// Grows the backing store by doubling (from <see cref="InitialCapacity"/> on the first
    /// growth), clamped to <see cref="Array.MaxLength"/> with guaranteed forward progress,
    /// the <see cref="PriorityQueue{TElement, TPriority}"/> <c>Grow</c> precedent.
    /// </summary>
    private void Grow()
    {
        const int GrowFactor = 2;
        const int MinimumGrow = 4;

        int newcapacity = GrowFactor * _nodes.Length;

        // Allow the heap to grow to the maximum possible capacity before encountering overflow
        // (the BCL PriorityQueue.Grow clamp): without this, doubling past 2^30 entries would
        // overflow negative and surface as a wrong-typed ArgumentOutOfRangeException.
        if ((uint)newcapacity > Array.MaxLength)
        {
            newcapacity = Array.MaxLength;
        }

        // Guarantee forward progress (BCL MinimumGrow); the first growth allocates
        // InitialCapacity outright.
        newcapacity = Math.Max(newcapacity, _nodes.Length == 0 ? InitialCapacity : _nodes.Length + MinimumGrow);

        Array.Resize(ref _nodes, newcapacity);
    }

    /// <summary>
    /// Hole-based sift-up on the devirtualized default-comparer path: walks the moving entry toward
    /// the root, shifting larger ancestors down into the hole, and places it once at the end.
    /// Mirrors <c>PriorityQueue.MoveUpDefaultComparer</c>.
    /// </summary>
    private void MoveUpDefaultComparer(int index, TElement element, TPriority priority)
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
    }

    /// <summary>
    /// Hole-based sift-up on the cached-comparer path (custom comparer, or any reference-type
    /// priority): identical structure to <see cref="MoveUpDefaultComparer"/> but ordered through the
    /// stored comparer field. Mirrors <c>PriorityQueue.MoveUpCustomComparer</c>.
    /// </summary>
    private void MoveUpCustomComparer(int index, TElement element, TPriority priority)
    {
        IComparer<TPriority> cmp = _comparer!;
        (TElement Element, TPriority Priority)[] nodes = _nodes;

        while (index > 0)
        {
            int parent = (index - 1) >> 2;

            if (cmp.Compare(priority, nodes[parent].Priority) >= 0)
            {
                break;
            }

            nodes[index] = nodes[parent];
            index = parent;
        }

        nodes[index] = (element, priority);
    }

    /// <summary>
    /// Hole-based sift-down on the devirtualized default-comparer path: repeatedly finds the
    /// smallest of the (up to four) children, and if it is smaller than the moving entry, shifts it
    /// into the hole; otherwise the moving entry is placed and the walk stops.
    /// Mirrors <c>PriorityQueue.MoveDownDefaultComparer</c>.
    /// </summary>
    private void MoveDownDefaultComparer(int index, TElement element, TPriority priority)
    {
        (TElement Element, TPriority Priority)[] nodes = _nodes;
        int count = _size;

        while (true)
        {
            int firstChild = (index << 2) + 1;
            if (firstChild >= count)
            {
                break;
            }

            // Find the minimum child among the up-to-four children [firstChild, lastChild].
            int minChild = firstChild;
            TPriority minPriority = nodes[firstChild].Priority;
            int lastChild = Math.Min(firstChild + 3, count - 1);

            for (int child = firstChild + 1; child <= lastChild; child++)
            {
                TPriority candidate = nodes[child].Priority;
                if (Comparer<TPriority>.Default.Compare(candidate, minPriority) < 0)
                {
                    minChild = child;
                    minPriority = candidate;
                }
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

    /// <summary>
    /// Hole-based sift-down on the cached-comparer path: identical structure to
    /// <see cref="MoveDownDefaultComparer"/> but ordered through the stored comparer field.
    /// Mirrors <c>PriorityQueue.MoveDownCustomComparer</c>.
    /// </summary>
    private void MoveDownCustomComparer(int index, TElement element, TPriority priority)
    {
        IComparer<TPriority> cmp = _comparer!;
        (TElement Element, TPriority Priority)[] nodes = _nodes;
        int count = _size;

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
                if (cmp.Compare(candidate, minPriority) < 0)
                {
                    minChild = child;
                    minPriority = candidate;
                }
            }

            if (cmp.Compare(minPriority, priority) >= 0)
            {
                break;
            }

            nodes[index] = nodes[minChild];
            index = minChild;
        }

        nodes[index] = (element, priority);
    }

    // ---- Locked composition: heap + seqlock + striped count (DR-4/DR-5 integration) ----
    //
    // These are the only mutation entry points callers should use. Each TryEnters SyncLock,
    // never blocking on contention (DR-7's "wait-free locking": a contended sub-queue means
    // another thread is making progress there, so the caller resamples instead of waiting), and
    // under the lock composes a heap operation with a CONDITIONAL top publication and a
    // volatile striped-count update.
    //
    // Publish elision is the point of the composition: the seqlock is rewritten only when the
    // heap MINIMUM PRIORITY actually changes. A non-minimum push and a pop that exposes an
    // equal-priority duplicate root both leave the published priority semantically correct, so
    // they skip the version-bumping write entirely. Foreign samplers' cached copies of the
    // version/top lines then stay valid, avoiding cache-line ping-pong in steady-state mixed and
    // narrow-key-range workloads. The SubQueueTests version-counter assertions pin this.

    /// <summary>
    /// Attempts to acquire the sub-queue lock and push an entry, maintaining the published top
    /// and the striped count. Never blocks: a contended lock yields an immediate
    /// <see langword="false"/> so the caller can resample another sub-queue (DR-7).
    /// </summary>
    /// <param name="element">The element to store.</param>
    /// <param name="priority">The priority that orders the entry.</param>
    /// <returns>
    /// <see langword="true"/> when the entry was pushed; <see langword="false"/> when the lock
    /// was contended and nothing was done.
    /// </returns>
    internal bool TryLockedPush(TElement element, TPriority priority)
    {
        if (!SyncLock.TryEnter())
        {
            return false;
        }

        try
        {
            bool wasEmpty = _size == 0;
            TPriority previousRoot = wasEmpty ? default! : _nodes[0].Priority;

            HeapPush(element, priority);

            // Republish only when the minimum changed: first entry, or strictly smaller than
            // the previous root. An equal-priority push leaves the published value correct.
            if (wasEmpty || CompareEffective(priority, previousRoot) < 0)
            {
                Debug.Assert(
                    CompareEffective(_nodes[0].Priority, priority) == 0,
                    "Publishing a pushed priority that did not become the heap root.");
                PublishTop(priority, empty: false);
            }

            Volatile.Write(ref _header.Count, _size);
            return true;
        }
        finally
        {
            SyncLock.Exit();
        }
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
    /// was observed (the caller resamples; a contended queue means progress is being made).
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
            if (!TryHeapPop(out element, out priority))
            {
                return SubQueuePopStatus.Empty;
            }

            if (_size == 0)
            {
                PublishTop(priority, empty: true);
            }
            else
            {
                // Republish only when the exposed root's priority differs from the popped one;
                // a duplicate minimum leaves the published priority value already correct.
                TPriority newRoot = _nodes[0].Priority;
                if (CompareEffective(newRoot, priority) != 0)
                {
                    PublishTop(newRoot, empty: false);
                }
            }

            Volatile.Write(ref _header.Count, _size);
            return SubQueuePopStatus.Success;
        }
        finally
        {
            SyncLock.Exit();
        }
    }

    /// <summary>
    /// Empties this sub-queue under its lock, publishing the empty state and zeroing the striped
    /// count, and returns how many entries were removed so the caller can release that many
    /// bounded-capacity reservations. It uses a blocking <c>lock</c> rather than a <c>TryEnter</c>,
    /// as with <see cref="SnapshotTo"/>: <c>Clear</c> is not a hot path and the critical section is
    /// a bounded array clear.
    /// </summary>
    /// <returns>The number of entries removed from this sub-queue.</returns>
    internal int LockedClear()
    {
        lock (SyncLock)
        {
            int removed = _size;
            if (removed > 0)
            {
                // Gated clear: only release references; value-type-only entries skip the writes.
                if (RuntimeHelpers.IsReferenceOrContainsReferences<(TElement, TPriority)>())
                {
                    Array.Clear(_nodes, 0, removed);
                }

                _size = 0;
                PublishTop(default!, empty: true);
                Volatile.Write(ref _header.Count, 0);
            }

            return removed;
        }
    }

    /// <summary>
    /// Compares two priorities through the DR-6 dual path: the devirtualized
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

    // ---- ToArray / enumeration support (DR-14) ----

    /// <summary>
    /// Copies this sub-queue's entries under its lock into <paramref name="buffer"/>. ToArray and
    /// enumeration support (DR-14): a brief per-queue lock, copied one sub-queue at a time with no
    /// global freeze and no cross-queue consistency claim. It uses a blocking <c>lock</c> rather
    /// than a <c>TryEnter</c> here: enumeration is not a hot path, the design specifies "taking each
    /// lock briefly", and the critical section is a pure array copy.
    /// </summary>
    /// <param name="buffer">The destination list that receives this sub-queue's live entries.</param>
    internal void SnapshotTo(List<(TElement Element, TPriority Priority)> buffer)
    {
        lock (SyncLock)
        {
            for (int i = 0; i < _size; i++)
            {
                buffer.Add(_nodes[i]);
            }
        }
    }
}
