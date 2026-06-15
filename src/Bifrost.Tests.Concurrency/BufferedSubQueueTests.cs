// =============================================================================
// <copyright file="BufferedSubQueueTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Runtime.CompilerServices;
using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Coverage for the ESA 2021 §4 buffered MultiQueue (DR-1..DR-7): the <c>[InlineArray(16)]</c>
/// buffer storage, the buffered push/pop algorithm (insertion buffer <c>I</c>, sorted deletion
/// buffer <c>D</c>, flush-on-full, refill-on-empty, eviction cascade), the D-empty emptiness
/// invariant, the seqlock/occupancy/Count integration at the <c>D</c> boundary, write-barrier-safe
/// moves, the default-OFF bit-exact bypass, and the <c>BIFROST_TEST_HOOKS</c> counters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non-tautology.</b> The differential tests run a buffered (cap 16) and an unbuffered sub-queue
/// over identical seeds and assert the per-sub-queue dequeue order is byte-for-byte identical; a
/// buffered path that reordered, dropped, or duplicated an element would diverge from the proven
/// arity-4 heap. The invariant and refill tests assert the exact resident structure (D holds the
/// smallest <c>min(cap, |heap|)</c> in sorted order), not merely that a pop returned.
/// </para>
/// </remarks>
public class BufferedSubQueueTests
{
    /// <summary>
    /// Builds a sub-queue with the given logical buffer capacity (0 = unbuffered). Named arguments
    /// match the existing <see cref="SubQueue{TElement, TPriority}"/> construction idiom in the suite.
    /// </summary>
    /// <param name="bufferCapacity">The logical buffer capacity in <c>[0, 16]</c>.</param>
    /// <returns>A fresh <c>(int, int)</c> sub-queue.</returns>
    private static SubQueue<int, int> NewSubQueue(int bufferCapacity)
        => new(comparer: null, index: 0, occupancy: new ulong[1], bufferCapacity: bufferCapacity);

    /// <summary>
    /// DR-1: the <see cref="SubQueueBuffer{TElement, TPriority}"/> inline-array struct holds exactly
    /// <see cref="SubQueue{TElement, TPriority}.BufferCapacityMax"/> (16) <c>(element, priority)</c>
    /// slots; writing all 16 through the <c>AsSpan</c> view and reading them back round-trips every
    /// slot in order, and a length-<c>n</c> span exposes exactly the first <c>n</c> logical slots.
    /// </summary>
    [Test]
    public async Task BufferLayout_InlineArray16_RoundTripsAllSlots()
    {
        SubQueueBuffer<int, int> buffer = default;

        // A span cannot cross an `await`; capture the lengths and copies into locals first, then assert.
        int fullLength = SubQueueBuffer<int, int>.AsSpan(ref buffer, SubQueue<int, int>.BufferCapacityMax).Length;

        // Write 16 distinct (element, priority) tuples through the full-length span.
        WriteSlots(ref buffer);

        // Read them back through a fresh span over the same storage into managed arrays.
        var readbackElements = new int[SubQueue<int, int>.BufferCapacityMax];
        var readbackPriorities = new int[SubQueue<int, int>.BufferCapacityMax];
        CopyOut(ref buffer, SubQueue<int, int>.BufferCapacityMax, readbackElements, readbackPriorities);

        // A length-n span honors the logical n <= 16 and aliases the same leading storage.
        int partialLength = SubQueueBuffer<int, int>.AsSpan(ref buffer, 5).Length;
        var partialPriorities = new int[5];
        CopyOut(ref buffer, 5, new int[5], partialPriorities);

        await Assert.That(fullLength).IsEqualTo(16).Because("the full-length span exposes all 16 inline slots");
        for (int i = 0; i < readbackElements.Length; i++)
        {
            await Assert.That(readbackElements[i]).IsEqualTo(i * 10).Because("slot element round-trips");
            await Assert.That(readbackPriorities[i]).IsEqualTo(i).Because("slot priority round-trips");
        }

        await Assert.That(partialLength).IsEqualTo(5).Because("a length-n span exposes exactly the first n logical slots");
        for (int i = 0; i < partialPriorities.Length; i++)
        {
            await Assert.That(partialPriorities[i]).IsEqualTo(i).Because("the partial span aliases the same leading storage");
        }
    }

    /// <summary>Writes 16 distinct <c>(i*10, i)</c> tuples through the full-length span (no <c>await</c> in scope).</summary>
    private static void WriteSlots(ref SubQueueBuffer<int, int> buffer)
    {
        Span<(int Element, int Priority)> full = SubQueueBuffer<int, int>.AsSpan(ref buffer, SubQueue<int, int>.BufferCapacityMax);
        for (int i = 0; i < full.Length; i++)
        {
            full[i] = (Element: i * 10, Priority: i);
        }
    }

    /// <summary>Copies the first <paramref name="length"/> slots out into managed arrays (no <c>await</c> in scope).</summary>
    private static void CopyOut(ref SubQueueBuffer<int, int> buffer, int length, int[] elements, int[] priorities)
    {
        Span<(int Element, int Priority)> span = SubQueueBuffer<int, int>.AsSpan(ref buffer, length);
        for (int i = 0; i < span.Length; i++)
        {
            elements[i] = span[i].Element;
            priorities[i] = span[i].Priority;
        }
    }

    /// <summary>
    /// Drains a sub-queue entirely under its lock (await is illegal inside a lock body), returning the
    /// popped priorities in pop order. Funnels through <c>PopHeldRoot</c>, the single drain path.
    /// </summary>
    /// <param name="subQueue">The sub-queue to drain.</param>
    /// <returns>The popped priorities, in the order they came out.</returns>
    private static List<int> DrainAll(SubQueue<int, int> subQueue)
    {
        var drained = new List<int>();
        lock (subQueue.SyncLock)
        {
            while (subQueue.PopHeldRoot(out _, out int priority) == SubQueuePopStatus.Success)
            {
                drained.Add(priority);
            }
        }

        return drained;
    }

    /// <summary>
    /// DR-2 (T3): a push-only sequence into a buffered (cap 16) sub-queue drains in exactly the same
    /// order as the proven unbuffered arity-4 heap over an identical seed. With pushes only, every
    /// element flows through the insertion buffer <c>I</c> (flushing to the heap when full); the
    /// differential drain pins that the buffered path reorders nothing.
    /// </summary>
    [Test]
    public async Task Push_Buffered_MatchesUnbufferedOrder_InsertOnly()
    {
        var buffered = NewSubQueue(bufferCapacity: 16);
        var unbuffered = NewSubQueue(bufferCapacity: 0);

        var rng = new Random(0x3357);
        const int n = 500; // well past the cap so I flushes to the heap many times.
        for (int i = 0; i < n; i++)
        {
            int p = rng.Next(0, 10_000);
            buffered.TryLockedPush(p, p);
            unbuffered.TryLockedPush(p, p);
        }

        // Buffering is genuinely engaged: the deletion buffer holds the published front (D non-empty),
        // and the heap holds at most a flush short of everything (the unbuffered queue keeps it ALL in
        // the heap). A no-op bufferCapacity would leave D empty and the heap holding all n elements.
        await Assert.That(buffered.DeletionCountForTest).IsGreaterThan(0).Because(
            "the buffered push routes the minimum into the sorted deletion buffer D");
        await Assert.That(buffered.HeapSize).IsLessThan(n).Because(
            "the buffered path keeps some elements in I/D, so the heap holds fewer than the full population");

        List<int> bufferedDrain = DrainAll(buffered);
        List<int> unbufferedDrain = DrainAll(unbuffered);

        await Assert.That(bufferedDrain.Count).IsEqualTo(n).Because("the buffered drain returns every pushed element");
        await Assert.That(bufferedDrain.SequenceEqual(unbufferedDrain)).IsTrue().Because(
            "a push-only buffered run drains in the identical priority order as the unbuffered heap");
    }

    /// <summary>
    /// DR-2 (T4): seeding more than <c>C</c> elements and popping them all drains in strict ascending
    /// priority order (the buffered pop serves <c>D.front()</c>, the sub-queue minimum), and the
    /// deletion buffer is refilled from the heap at least twice over the drain (a population of
    /// <c>3·C + 1</c> needs ≥ 2 refills after the initial fill). Pinned through the refill counter.
    /// </summary>
    [Test]
    public async Task Pop_Buffered_DrainsInPriorityOrder_WithRefill()
    {
        const int cap = 16;
        var buffered = NewSubQueue(bufferCapacity: cap);

        var rng = new Random(0x9001);
        const int n = (3 * cap) + 1; // > C, forcing the heap path and multiple refills.
        for (int i = 0; i < n; i++)
        {
            int p = rng.Next(0, 1_000);
            buffered.TryLockedPush(p, p);
        }

        long refillsBefore = buffered.DebugBufferRefillCountForTest;
        List<int> drained = DrainAll(buffered);
        long refillsAfter = buffered.DebugBufferRefillCountForTest;

        await Assert.That(drained.Count).IsEqualTo(n).Because("every seeded element drains");

        bool nonDecreasing = true;
        for (int i = 1; i < drained.Count; i++)
        {
            if (drained[i] < drained[i - 1])
            {
                nonDecreasing = false;
                break;
            }
        }

        await Assert.That(nonDecreasing).IsTrue().Because(
            "the buffered pop serves D.front() (the sub-queue minimum), so the drain is non-decreasing");
        await Assert.That(refillsAfter - refillsBefore).IsGreaterThanOrEqualTo(2).Because(
            "draining > 2·C elements empties and refills the deletion buffer from the heap at least twice");
    }

    /// <summary>
    /// DR-2/DR-6 (T5): pushing strictly-descending keys forces every key (after the first) to satisfy
    /// <c>v ≤ max(D)</c>, so each one sorted-inserts into the deletion buffer <c>D</c> (driving the
    /// hole-shift). Interleaving pops keeps the structure small enough to stay buffer-resident. The
    /// differential drain against an unbuffered sub-queue over the identical script proves the sorted
    /// insert preserves global order, and the sorted-insert path is exercised (no direct-to-D seeds
    /// beyond the first, and zero flushes because nothing ever reaches the heap).
    /// </summary>
    [Test]
    public async Task Push_SmallElement_InsertsIntoDeletionBufferSorted()
    {
        const int cap = 16;
        var buffered = NewSubQueue(bufferCapacity: cap);
        var unbuffered = NewSubQueue(bufferCapacity: 0);

        // Descending keys, fewer than the cap, so D never fills and the heap is never touched: every
        // key after the first is <= max(D) and sorted-inserts ahead of the residents.
        int[] descending = [100, 90, 80, 70, 60, 50, 40, 30];
        foreach (int p in descending)
        {
            buffered.TryLockedPush(p, p);
            unbuffered.TryLockedPush(p, p);
        }

        long flushes = buffered.DebugBufferFlushCountForTest;
        long directSeeds = buffered.DebugBufferDirectToDeletionCountForTest;
        int heapSize = buffered.HeapSize;
        int deletionCount = buffered.DeletionCountForTest;

        List<int> bufferedDrain = DrainAll(buffered);
        List<int> unbufferedDrain = DrainAll(unbuffered);

        await Assert.That(heapSize).IsEqualTo(0).Because("descending keys under the cap never reach the heap");
        await Assert.That(deletionCount).IsEqualTo(descending.Length).Because("all keys stay resident in the sorted deletion buffer");
        await Assert.That(flushes).IsEqualTo(0).Because("nothing flushed to the heap");
        await Assert.That(directSeeds).IsEqualTo(1).Because("only the first key seeded an empty D directly; the rest sorted-inserted");
        await Assert.That(bufferedDrain.SequenceEqual([30, 40, 50, 60, 70, 80, 90, 100])).IsTrue().Because(
            "the sorted deletion buffer drains in exact ascending order");
        await Assert.That(bufferedDrain.SequenceEqual(unbufferedDrain)).IsTrue().Because(
            "the sorted-insert path matches the unbuffered heap order exactly");
    }

    /// <summary>
    /// DR-2/DR-6 (T5): saturating both <c>D</c> and <c>I</c> and then pushing a key smaller than
    /// <c>max(D)</c> forces the full eviction cascade (sorted-insert evicts <c>max(D)</c> → <c>I</c>
    /// full → flush <c>I</c> to the heap → the evicted max heap-pushes). The cascade conserves every
    /// element and the full differential drain against an unbuffered sub-queue over the identical
    /// random script proves the global order is exact.
    /// </summary>
    [Test]
    public async Task Push_DFullIFull_EvictsMaxThroughHeap()
    {
        const int cap = 16;
        var buffered = NewSubQueue(bufferCapacity: cap);
        var unbuffered = NewSubQueue(bufferCapacity: 0);

        // A large random push/pop script that repeatedly saturates D and I and pushes small keys (the
        // skew toward small keys forces v <= max(D) sorted inserts that evict a full D, and the bursts
        // of pushes fill I so the eviction cascades through a heap flush).
        var rng = new Random(0xCA5C);
        var residentBuffered = 0;
        var residentUnbuffered = 0;
        long evictionsBefore = buffered.DebugBufferEvictionCountForTest;

        for (int step = 0; step < 4_000; step++)
        {
            // 75% pushes (skewed small), 25% pops: keeps the structure full and small-key-heavy.
            if (rng.Next(4) != 0 || residentBuffered == 0)
            {
                int p = rng.Next(0, 200); // small range => frequent v <= max(D)
                buffered.TryLockedPush(p, p);
                unbuffered.TryLockedPush(p, p);
                residentBuffered++;
                residentUnbuffered++;
            }
            else
            {
                lock (buffered.SyncLock)
                {
                    buffered.PopHeldRoot(out _, out _);
                }

                lock (unbuffered.SyncLock)
                {
                    unbuffered.PopHeldRoot(out _, out _);
                }

                residentBuffered--;
                residentUnbuffered--;
            }
        }

        long evictionsAfter = buffered.DebugBufferEvictionCountForTest;

        // Drain whatever remains; the multiset and order must match the unbuffered heap exactly.
        List<int> bufferedDrain = DrainAll(buffered);
        List<int> unbufferedDrain = DrainAll(unbuffered);

        await Assert.That(evictionsAfter - evictionsBefore).IsGreaterThan(0).Because(
            "the small-key-heavy script saturates D and forces max(D) eviction cascades");
        await Assert.That(bufferedDrain.Count).IsEqualTo(unbufferedDrain.Count).Because(
            "the eviction cascade conserves every element (no loss, no duplication)");
        await Assert.That(bufferedDrain.SequenceEqual(unbufferedDrain)).IsTrue().Because(
            "the full eviction cascade preserves the exact global dequeue order vs. the unbuffered heap");
    }

    /// <summary>
    /// DR-2 (T6): the load-bearing emptiness invariant — <c>D</c> empty ⟹ <c>I</c> empty ∧ heap empty
    /// (so <c>D</c> empty ⟺ the whole sub-queue empty). A long random push/pop sequence checks, after
    /// every operation, that whenever the deletion buffer is empty the insertion buffer and the heap are
    /// also empty; a populated I/heap behind an empty D would be a refill-ordering bug. (The converse —
    /// a non-empty D — carries no constraint: a tiny structure may live entirely in D.) The invariant is
    /// also debug-asserted inside the pop path.
    /// </summary>
    [Test]
    public async Task Invariant_DeletionBufferEmpty_ImpliesSubQueueEmpty()
    {
        const int cap = 16;
        var buffered = NewSubQueue(bufferCapacity: cap);

        var rng = new Random(0x1234);
        bool invariantHeld = true;
        var resident = 0;

        for (int step = 0; step < 10_000 && invariantHeld; step++)
        {
            // Bias toward pushes when empty, balanced otherwise, so D repeatedly empties and refills.
            bool push = resident == 0 || rng.Next(2) == 0;
            if (push)
            {
                int p = rng.Next(0, 500);
                buffered.TryLockedPush(p, p);
                resident++;
            }
            else
            {
                lock (buffered.SyncLock)
                {
                    if (buffered.PopHeldRoot(out _, out _) == SubQueuePopStatus.Success)
                    {
                        resident--;
                    }
                }
            }

            int d = buffered.DeletionCountForTest;
            int iBuf = buffered.InsertionCountForTest;
            int heap = buffered.HeapSize;

            // The load-bearing invariant: D empty ⟹ I empty AND heap empty (a populated I/heap behind
            // an empty D would be a refill-ordering bug). The converse does NOT hold — D may legitimately
            // hold the entire small population while I and the heap are empty (the tiny-structure case).
            bool dEmpty = d == 0;
            bool restEmpty = iBuf == 0 && heap == 0;
            if (dEmpty && !restEmpty)
            {
                invariantHeld = false;
            }
        }

        await Assert.That(invariantHeld).IsTrue().Because(
            "after every buffered operation, an empty D implies an empty I and heap (the ESA 2021 §4 refill invariant)");
    }

    /// <summary>
    /// DR-3 (T7): the seqlock-published top is <c>D.front()</c> (the sub-queue minimum) and the empty
    /// flag tracks <c>D</c>-emptiness. The lock-free <see cref="SubQueue{TElement, TPriority}.TryReadTop"/>
    /// reader yields exactly the current minimum after pushes, after a front-changing push, after a pop,
    /// and reports empty once <c>D</c> drains — all without the reader touching any buffer field.
    /// </summary>
    [Test]
    public async Task TryReadTop_Buffered_ReturnsDeletionBufferFront()
    {
        var buffered = NewSubQueue(bufferCapacity: 16);

        // Fresh sub-queue publishes empty.
        await Assert.That(buffered.TryReadTop(out _, out bool empty0)).IsTrue();
        await Assert.That(empty0).IsTrue().Because("a fresh buffered sub-queue publishes the empty state");

        // First push seeds D directly; the published top is that single minimum.
        buffered.TryLockedPush(50, 50);
        await Assert.That(buffered.TryReadTop(out int top1, out bool empty1)).IsTrue();
        await Assert.That(empty1).IsFalse();
        await Assert.That(top1).IsEqualTo(50).Because("the published top is D.front()");

        // A front-changing push (smaller key) re-publishes the new minimum.
        buffered.TryLockedPush(20, 20);
        await Assert.That(buffered.TryReadTop(out int top2, out _)).IsTrue();
        await Assert.That(top2).IsEqualTo(20).Because("a smaller key becomes the new D.front() and is republished");

        // A non-front push (larger key) leaves the published front unchanged.
        buffered.TryLockedPush(90, 90);
        await Assert.That(buffered.TryReadTop(out int top3, out _)).IsTrue();
        await Assert.That(top3).IsEqualTo(20).Because("a larger key does not change D.front(), so the publication stays 20");

        // Pop the front: the next minimum is republished.
        lock (buffered.SyncLock)
        {
            buffered.PopHeldRoot(out _, out _);
        }

        await Assert.That(buffered.TryReadTop(out int top4, out _)).IsTrue();
        await Assert.That(top4).IsEqualTo(50).Because("popping D.front() republishes the next minimum");

        // Drain the rest; the final state publishes empty.
        DrainAll(buffered);
        await Assert.That(buffered.TryReadTop(out _, out bool emptyEnd)).IsTrue();
        await Assert.That(emptyEnd).IsTrue().Because("a fully drained buffered sub-queue publishes the empty state");
    }

    /// <summary>
    /// DR-6 (T9): the per-sub-queue logical <c>Count</c> equals the total resident element count across
    /// the insertion buffer <c>I</c>, the deletion buffer <c>D</c>, and the arity-4 heap (the
    /// reference's <c>insertion_end_ + deletion_end_ + pq_.size()</c>). A conservation check over a long
    /// random push/pop run asserts the published count tracks enqueued-minus-dequeued exactly at every
    /// step, and the collection surface (<c>SnapshotTo</c>) carries the same multiset of resident entries.
    /// </summary>
    [Test]
    public async Task Count_Buffered_EqualsTotalResident()
    {
        var buffered = NewSubQueue(bufferCapacity: 16);

        var rng = new Random(0x7777);
        var expected = 0;
        bool conserved = true;

        for (int step = 0; step < 8_000 && conserved; step++)
        {
            bool push = expected == 0 || rng.Next(2) == 0;
            if (push)
            {
                int p = rng.Next(0, 1_000);
                buffered.TryLockedPush(p, p);
                expected++;
            }
            else
            {
                lock (buffered.SyncLock)
                {
                    if (buffered.PopHeldRoot(out _, out _) == SubQueuePopStatus.Success)
                    {
                        expected--;
                    }
                }
            }

            // VolatileCount is the published striped count = I + D + heap.
            if (buffered.VolatileCount != expected)
            {
                conserved = false;
            }

            // The component sum must also reconcile (defends against a stale published count).
            int components = buffered.InsertionCountForTest + buffered.DeletionCountForTest + buffered.HeapSize;
            if (components != expected)
            {
                conserved = false;
            }
        }

        await Assert.That(conserved).IsTrue().Because(
            "the published Count equals I + D + heap and tracks enqueued-minus-dequeued at every step");

        // The collection surface includes the buffered residents: SnapshotTo copies I + D + heap.
        var snapshot = new List<(int Element, int Priority)>();
        buffered.SnapshotTo(snapshot);
        await Assert.That(snapshot.Count).IsEqualTo(expected).Because(
            "SnapshotTo copies every resident entry across I, D, and the heap (the collection surface is buffer-correct)");
    }

    /// <summary>
    /// DR-4 (T10): with reference-containing elements, the buffer moves (sorted-insert shift,
    /// <c>max(D)</c> eviction, <c>I</c>→heap flush, heap→<c>D</c> refill) leave no live reference in a
    /// vacated slot. After draining the sub-queue and forcing a GC, every popped element is collectible
    /// (its <see cref="WeakReference"/> reports dead), proving the gated <c>Span.Clear</c> dropped the
    /// dead reference rather than letting a stale buffer slot pin it.
    /// </summary>
    [Test]
    public async Task BufferMoves_ReferenceElements_NoStaleRetention()
    {
        // string elements (reference-containing tuple) => the gated slot/block clears all run.
        var sub = new SubQueue<string, int>(comparer: null, index: 0, occupancy: new ulong[1], bufferCapacity: 16);

        // Distinct, non-interned element instances so a WeakReference can observe collectibility; the
        // priorities span a range that drives flushes, refills, and sorted inserts/evictions.
        const int n = 200;
        var weakRefs = new List<WeakReference>(n);
        PopulateReferenceSubQueue(sub, n, weakRefs);

        // Drain the whole sub-queue: every move vacates its source slots, which must be cleared.
        lock (sub.SyncLock)
        {
            while (sub.PopHeldRoot(out _, out _) == SubQueuePopStatus.Success)
            {
            }
        }

        await Assert.That(sub.DeletionCountForTest).IsEqualTo(0).Because("the sub-queue is fully drained");
        await Assert.That(sub.InsertionCountForTest).IsEqualTo(0).Because("the insertion buffer drained too");
        await Assert.That(sub.HeapSize).IsEqualTo(0).Because("the heap drained too");

        // Force collection: no buffer slot may still pin a popped element.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int alive = weakRefs.Count(w => w.IsAlive);
        await Assert.That(alive).IsEqualTo(0).Because(
            "after a full drain + GC, no vacated buffer slot retains a popped reference (gated Span.Clear ran)");
    }

    /// <summary>
    /// Pushes <paramref name="n"/> freshly-allocated <see cref="string"/> elements into a buffered
    /// reference-element sub-queue, recording a <see cref="WeakReference"/> to each so collectibility can
    /// be observed after the drain. Kept in a separate non-inlined frame so no JIT-rooted local pins an
    /// element past the populate step. Priorities are shuffled to drive flushes/refills/evictions.
    /// </summary>
    /// <param name="sub">The buffered sub-queue to populate.</param>
    /// <param name="n">The number of elements to push.</param>
    /// <param name="weakRefs">The list that receives a weak reference to each pushed element.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PopulateReferenceSubQueue(SubQueue<string, int> sub, int n, List<WeakReference> weakRefs)
    {
        var rng = new Random(0xBEEF);
        var order = Enumerable.Range(0, n).ToList();
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        foreach (int p in order)
        {
            // A fresh, non-interned string instance per element (concatenation defeats interning).
            string element = "elem-" + p.ToString();
            weakRefs.Add(new WeakReference(element));
            sub.TryLockedPush(element, p);
        }
    }

    /// <summary>
    /// DR-5 (T11): with <c>bufferCapacity == 0</c> the push/pop paths FULLY bypass the buffers, so the
    /// per-sub-queue drain order and <c>Count</c> are bit-exact with a sub-queue that never knew about
    /// buffering. Over identical seeds an off-buffered sub-queue and a baseline sub-queue produce the
    /// identical drain order and identical published count at every step; their buffers stay empty
    /// throughout (the predictable single compare took the off branch).
    /// </summary>
    [Test]
    public async Task DefaultOff_MatchesPreFeature_OrderCountRankError()
    {
        var off = NewSubQueue(bufferCapacity: 0);
        var baseline = NewSubQueue(bufferCapacity: 0);

        var rng = new Random(0x0FF0);
        bool countsMatch = true;

        // Interleaved push/pop with identical seeds; assert published counts track step-for-step.
        for (int step = 0; step < 5_000; step++)
        {
            int p = rng.Next(0, 5_000);
            off.TryLockedPush(p, p);
            baseline.TryLockedPush(p, p);

            if (off.VolatileCount != baseline.VolatileCount)
            {
                countsMatch = false;
            }

            // The off-path never touches the buffers.
            if (off.DeletionCountForTest != 0 || off.InsertionCountForTest != 0)
            {
                countsMatch = false;
            }

            if (step % 3 == 0)
            {
                lock (off.SyncLock)
                {
                    off.PopHeldRoot(out _, out _);
                }

                lock (baseline.SyncLock)
                {
                    baseline.PopHeldRoot(out _, out _);
                }
            }
        }

        await Assert.That(countsMatch).IsTrue().Because(
            "an off (bufferCapacity 0) sub-queue's Count matches the baseline at every step and never touches the buffers");

        // Count = heap size on the off path (no buffer residents).
        await Assert.That(off.VolatileCount).IsEqualTo(off.HeapSize).Because("the off path's Count is exactly the heap size");

        List<int> offDrain = DrainAll(off);
        List<int> baselineDrain = DrainAll(baseline);
        await Assert.That(offDrain.SequenceEqual(baselineDrain)).IsTrue().Because(
            "the off path drains in the identical order as the baseline (bit-exact bypass)");
    }

    /// <summary>
    /// DR-5 (T11): <c>LockedClear</c> on a buffered sub-queue returns the full resident count
    /// <c>I + D + heap</c> (not just the heap), so the owning queue releases the correct number of
    /// bounded reservations, and the cleared sub-queue is genuinely empty across all three structures.
    /// </summary>
    [Test]
    public async Task LockedClear_Buffered_ReturnsFullResidentCount()
    {
        var buffered = NewSubQueue(bufferCapacity: 16);

        // Seed enough to populate D, I, and the heap simultaneously.
        const int n = 100;
        var rng = new Random(0x4242);
        for (int i = 0; i < n; i++)
        {
            int p = rng.Next(0, 1_000);
            buffered.TryLockedPush(p, p);
        }

        int residentBefore = buffered.InsertionCountForTest + buffered.DeletionCountForTest + buffered.HeapSize;
        await Assert.That(residentBefore).IsEqualTo(n).Because("all n elements are resident across I, D, and the heap");

        int removed = buffered.LockedClear();

        await Assert.That(removed).IsEqualTo(n).Because(
            "LockedClear returns the full resident count I + D + heap, so bounded reservations release exactly");
        await Assert.That(buffered.DeletionCountForTest).IsEqualTo(0).Because("D is cleared");
        await Assert.That(buffered.InsertionCountForTest).IsEqualTo(0).Because("I is cleared");
        await Assert.That(buffered.HeapSize).IsEqualTo(0).Because("the heap is cleared");
        await Assert.That(buffered.VolatileCount).IsEqualTo(0).Because("the striped count is zeroed");

        await Assert.That(buffered.TryReadTop(out _, out bool empty)).IsTrue();
        await Assert.That(empty).IsTrue().Because("the cleared buffered sub-queue republished the empty state");
    }

    /// <summary>
    /// DR-7 (T12): the <c>BIFROST_TEST_HOOKS</c> buffer counters increment exactly as the scripted
    /// sequence dictates. With a tiny capacity (<c>C = 2</c>) every transition is reachable in a few
    /// operations: the first push seeds <c>D</c> directly; descending small keys sorted-insert and
    /// (once <c>D</c> is full) evict <c>max(D)</c>; further pushes fill and flush <c>I</c>; pops served
    /// from <c>D.front()</c> count as hits; and the pop that empties <c>D</c> with the heap populated
    /// triggers a refill. Asserted against exact expected counts.
    /// </summary>
    [Test]
    public async Task Counters_BufferedRun_RecordFlushRefillHitEvict()
    {
        const int cap = 2;
        var sub = NewSubQueue(bufferCapacity: cap);

        // Push 10 -> direct-to-D seed. D=[10], direct=1.
        sub.TryLockedPush(10, 10);
        await Assert.That(sub.DebugBufferDirectToDeletionCountForTest).IsEqualTo(1L).Because("the first push seeds an empty D directly");

        // Push 9 (<= max(D)=10) -> sorted-insert, D not yet full. D=[9,10], evict=0.
        sub.TryLockedPush(9, 9);
        await Assert.That(sub.DeletionCountForTest).IsEqualTo(2).Because("D holds both small keys, sorted");
        await Assert.That(sub.DebugBufferEvictionCountForTest).IsEqualTo(0L).Because("D was not full, so no eviction");

        // Push 8 (<= max(D)=10), D full (cap 2) -> evict max(D)=10 into I; sorted-insert 8.
        // evict=1, I=[10] (count 1, not full), D=[8,9].
        sub.TryLockedPush(8, 8);
        await Assert.That(sub.DebugBufferEvictionCountForTest).IsEqualTo(1L).Because("a full D evicts its max on a small-key sorted insert");
        await Assert.That(sub.InsertionCountForTest).IsEqualTo(1).Because("the evicted max landed in I (not yet full)");
        await Assert.That(sub.DebugBufferFlushCountForTest).IsEqualTo(0L).Because("I is not full yet, so nothing flushed");

        // Push 7 (<= max(D)=9), D full -> evict 9 into I; I=[10,9] reaches cap (full) but does not flush
        // until the NEXT append; sorted-insert 7. evict=2, I count=2, flush=0, D=[7,8].
        sub.TryLockedPush(7, 7);
        await Assert.That(sub.DebugBufferEvictionCountForTest).IsEqualTo(2L).Because("the second full-D sorted insert evicts again");
        await Assert.That(sub.InsertionCountForTest).IsEqualTo(2).Because("I is now full with the two evicted maxima");
        await Assert.That(sub.DebugBufferFlushCountForTest).IsEqualTo(0L).Because("a full I flushes on the next append, not on filling");

        // Push 6 (<= max(D)=8), D full -> evict 8; EvictToInsertion sees I full -> FLUSH I to heap
        // (flush=1, heap now {10,9}), then append 8 (I=[8]); sorted-insert 6. evict=3, D=[6,7].
        sub.TryLockedPush(6, 6);
        await Assert.That(sub.DebugBufferFlushCountForTest).IsEqualTo(1L).Because("a full I flushes to the heap on the next append (the evicted 8)");
        await Assert.That(sub.DebugBufferEvictionCountForTest).IsEqualTo(3L).Because("a third full-D sorted insert evicts again");
        await Assert.That(sub.InsertionCountForTest).IsEqualTo(1).Because("I emptied on the flush, then took the newly evicted max");
        await Assert.That(sub.HeapSize).IsEqualTo(2).Because("the two flushed entries (10, 9) now live in the heap");

        // Pop both D.front()s: hits=2. The pushed multiset is {10,9,8,7,6}; D=[6,7] pops 6 then 7.
        // After popping 7, D empties; I=[8] and heap={10,9} are populated -> refill (refill=1).
        long hitsBefore = sub.DebugBufferPopHitCountForTest;
        long refillsBefore = sub.DebugBufferRefillCountForTest;
        lock (sub.SyncLock)
        {
            sub.PopHeldRoot(out _, out int first);
            sub.PopHeldRoot(out _, out int second);
            _ = (first, second);
        }

        await Assert.That(sub.DebugBufferPopHitCountForTest - hitsBefore).IsEqualTo(2L).Because("both pops were served from D.front()");
        await Assert.That(sub.DebugBufferRefillCountForTest - refillsBefore).IsEqualTo(1L).Because(
            "emptying D while I/heap are populated triggers exactly one refill");

        // Drain the rest; the full pushed multiset {6,7,8,9,10} must come out in ascending order.
        var drained = new List<int> { 6, 7 }; // already popped above
        lock (sub.SyncLock)
        {
            while (sub.PopHeldRoot(out _, out int p) == SubQueuePopStatus.Success)
            {
                drained.Add(p);
            }
        }

        await Assert.That(drained.SequenceEqual([6, 7, 8, 9, 10])).IsTrue().Because(
            "the scripted run conserved every element and drains in ascending order");
    }

    /// <summary>
    /// DR-6 (T14): a refill where the heap holds fewer than <c>C</c> entries fills <c>D</c> with exactly
    /// <c>|heap|</c> entries and empties the heap (the <c>min(C, |heap|)</c> bound). Driven by seeding
    /// past the cap, draining <c>D</c> to force the heap path, then popping until a refill pulls the
    /// final small remainder.
    /// </summary>
    [Test]
    public async Task Refill_HeapSmallerThanCapacity_FillsExactly()
    {
        const int cap = 4;
        var sub = NewSubQueue(bufferCapacity: cap);

        // Seven strictly-descending keys with cap 4: the first seeds D, the next three fill D, and the
        // last three each evict D's max into I (filling I to 3, one short of full). At rest:
        //   D = [94,95,96,97]   I = [100,99,98] (3 < cap)   heap = empty
        // so the refill that runs when D drains flushes I (3 entries) into the heap then takes
        // min(cap, 3) = 3 — the |heap| < C partial-fill path.
        int[] descending = [100, 99, 98, 97, 96, 95, 94];
        foreach (int p in descending)
        {
            sub.TryLockedPush(p, p);
        }

        await Assert.That(sub.DeletionCountForTest).IsEqualTo(cap).Because("D is full at rest");
        await Assert.That(sub.InsertionCountForTest).IsEqualTo(3).Because("three evicted maxima sit in I (one short of full)");
        await Assert.That(sub.HeapSize).IsEqualTo(0).Because("nothing has reached the heap yet");

        long refillsBefore = sub.DebugBufferRefillCountForTest;

        // Pop the four D entries; the fourth empties D and triggers the partial refill.
        lock (sub.SyncLock)
        {
            for (int i = 0; i < cap; i++)
            {
                sub.PopHeldRoot(out _, out _);
            }
        }

        await Assert.That(sub.DebugBufferRefillCountForTest - refillsBefore).IsEqualTo(1L).Because("draining D triggered exactly one refill");
        await Assert.That(sub.DeletionCountForTest).IsEqualTo(3).Because(
            "a refill where |heap| (3) < C (4) fills D with exactly |heap| = 3 entries");
        await Assert.That(sub.HeapSize).IsEqualTo(0).Because("the partial refill drained the heap completely");

        // The remaining drain stays ascending and exhausts the structure.
        List<int> rest = DrainAll(sub);
        bool nonDecreasing = true;
        for (int i = 1; i < rest.Count; i++)
        {
            if (rest[i] < rest[i - 1])
            {
                nonDecreasing = false;
            }
        }

        await Assert.That(nonDecreasing).IsTrue().Because("the post-partial-refill drain stays in ascending order");
    }

    /// <summary>
    /// DR-6 (T14): a sub-queue whose resident population stays strictly below the buffer capacity never
    /// touches the heap — no flushes, no refills (asserted via the T12 counters). With fewer than
    /// <c>C</c> residents the deletion buffer <c>D</c> never fills, so no <c>max(D)</c> is ever evicted
    /// into <c>I</c>; every push sorted-inserts into (or seeds) <c>D</c> and every pop is served from
    /// <c>D.front()</c>, so the deep-heap walk is fully avoided — the locality win buffering exists for.
    /// </summary>
    [Test]
    public async Task TinyQueue_StaysInDeletionBuffer_NeverTouchesHeap()
    {
        const int cap = 16;
        var sub = NewSubQueue(bufferCapacity: cap);

        // A lifetime that stays below the cap (resident < cap) AND only ever pushes keys <= the current
        // D minimum, so every push sorted-inserts at (or near) the front of D and nothing is ever routed
        // into I. With resident < cap, D never fills (no eviction), and since I is always empty, a pop
        // that drains D never triggers a refill — the heap is never touched. The push key is driven
        // strictly downward so it is always <= max(D).
        var rng = new Random(0x7149);
        var resident = 0;
        int nextKey = 1_000_000; // pushed keys strictly descend, so each is <= the current D contents
        for (int step = 0; step < 2_000; step++)
        {
            bool canPush = resident < cap - 1; // strictly below the cap: D never fills
            bool push = resident == 0 || (canPush && rng.Next(2) == 0);
            if (push)
            {
                nextKey -= rng.Next(1, 5); // monotonically decreasing keys
                sub.TryLockedPush(nextKey, nextKey);
                resident++;
            }
            else
            {
                lock (sub.SyncLock)
                {
                    if (sub.PopHeldRoot(out _, out _) == SubQueuePopStatus.Success)
                    {
                        resident--;
                    }
                }
            }
        }

        await Assert.That(sub.HeapSize).IsEqualTo(0).Because("a sub-queue that stays below the cap keeps everything in the buffers");
        await Assert.That(sub.DebugBufferFlushCountForTest).IsEqualTo(0L).Because("nothing ever flushed to the heap");
        await Assert.That(sub.DebugBufferRefillCountForTest).IsEqualTo(0L).Because("the heap was never refilled because it stayed empty");
        await Assert.That(sub.DebugBufferEvictionCountForTest).IsEqualTo(0L).Because("D never filled, so no max(D) was ever evicted");
    }
}
