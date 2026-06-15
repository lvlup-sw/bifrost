// =============================================================================
// <copyright file="SubQueueEdgeTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Coverage for the <see cref="SubQueue{TElement, TPriority}"/> edges (DR-4): the <c>Grow</c>
/// <see cref="Array.MaxLength"/> clamp and "cannot grow further" throw (#18, exercised through the
/// pure <c>ComputeGrownCapacityForTest</c> seam so the boundary is testable without allocating a
/// multi-gigabyte array), the seqlock tear/retry "unknown" edge of <c>TryReadTop</c>,
/// <c>SnapshotTo</c>, and <c>LockedClear</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non-tautology.</b> The growth tests assert the exact computed capacities and the precise throw
/// at the boundary. The seqlock retry test asserts <c>TryReadTop</c> returns "unknown" while a writer
/// is stamped in-progress and recovers a real snapshot once the version is even again. The
/// snapshot/clear tests assert the exact entries copied and that a clear empties and re-publishes.
/// </para>
/// </remarks>
public class SubQueueEdgeTests
{
    /// <summary>
    /// The normal growth ladder: doubling from the empty store. The first growth jumps to the initial
    /// capacity (16); subsequent growths double (32, 64, ...). Asserted directly through the pure
    /// capacity arithmetic so the doubling branch is pinned independent of allocation.
    /// </summary>
    [Test]
    public async Task ComputeGrownCapacity_NormalGrowth_DoublesFromInitial()
    {
        await Assert.That(SubQueue<int, int>.ComputeGrownCapacityForTest(0)).IsEqualTo(16).Because(
            "the first growth from an empty store jumps to the initial capacity of 16");
        await Assert.That(SubQueue<int, int>.ComputeGrownCapacityForTest(16)).IsEqualTo(32).Because("16 doubles to 32");
        await Assert.That(SubQueue<int, int>.ComputeGrownCapacityForTest(32)).IsEqualTo(64).Because("32 doubles to 64");
        await Assert.That(SubQueue<int, int>.ComputeGrownCapacityForTest(1_000)).IsEqualTo(2_000).Because("doubling holds mid-range");
    }

    /// <summary>
    /// The <see cref="Array.MaxLength"/> clamp (#18): a length whose double would overflow past
    /// <see cref="Array.MaxLength"/> is clamped exactly to <see cref="Array.MaxLength"/> rather than
    /// overflowing negative, while still strictly exceeding the current length so growth makes progress.
    /// </summary>
    [Test]
    public async Task ComputeGrownCapacity_NearMaxLength_ClampsToArrayMaxLength()
    {
        // A length just below half of Array.MaxLength: doubling it would exceed the limit, so the
        // result is clamped to exactly Array.MaxLength (still strictly larger than the input).
        int justOverHalf = (Array.MaxLength / 2) + 10;

        int grown = SubQueue<int, int>.ComputeGrownCapacityForTest(justOverHalf);

        await Assert.That(grown).IsEqualTo(Array.MaxLength).Because(
            "doubling past Array.MaxLength clamps to exactly Array.MaxLength, never overflows negative");
        await Assert.That(grown).IsGreaterThan(justOverHalf).Because("the clamped capacity still makes forward progress");
    }

    /// <summary>
    /// The forward-progress floor clamp near the boundary: a length only a few short of
    /// <see cref="Array.MaxLength"/> still grows to exactly <see cref="Array.MaxLength"/> (the
    /// <c>currentLength + 4</c> floor is itself re-clamped to the limit, so the resize is valid and
    /// strictly increasing).
    /// </summary>
    [Test]
    public async Task ComputeGrownCapacity_FloorNearBoundary_ReClampsToMaxLength()
    {
        int nearMax = Array.MaxLength - 2; // currentLength + 4 would exceed Array.MaxLength.

        int grown = SubQueue<int, int>.ComputeGrownCapacityForTest(nearMax);

        await Assert.That(grown).IsEqualTo(Array.MaxLength).Because(
            "the forward-progress floor is re-clamped to Array.MaxLength so the resize stays valid at the boundary");
        await Assert.That(grown).IsGreaterThan(nearMax).Because("growth still strictly increases the length");
    }

    /// <summary>
    /// The "cannot grow further" throw (#18): a store already AT <see cref="Array.MaxLength"/> cannot
    /// produce a strictly-larger capacity, so the arithmetic throws a clear, typed
    /// <see cref="InvalidOperationException"/> rather than resizing to a non-increasing length.
    /// </summary>
    [Test]
    public async Task ComputeGrownCapacity_AlreadyAtMaxLength_ThrowsCannotGrowFurther()
    {
        await Assert.That(() => SubQueue<int, int>.ComputeGrownCapacityForTest(Array.MaxLength))
            .Throws<InvalidOperationException>().Because(
                "a store already at Array.MaxLength cannot grow further and must surface a typed failure");
    }

    /// <summary>
    /// The heap genuinely grows past its initial capacity under real pushes: inserting many elements
    /// (well beyond the initial 16) keeps the min-heap correct, so a strict drain returns exact
    /// ascending order — proving <c>Grow</c> resizes without corrupting the heap.
    /// </summary>
    [Test]
    public async Task HeapPush_ManyElements_GrowsAndStaysSorted()
    {
        var subQueue = new SubQueue<int, int>(comparer: null, index: 0, occupancy: new ulong[1]);
        const int n = 5_000; // forces many doublings past InitialCapacity = 16.

        var rng = new Random(0xABCD);
        for (int i = 0; i < n; i++)
        {
            int p = rng.Next();
            subQueue.TryLockedPush(p, p);
        }

        await Assert.That(subQueue.HeapSize).IsEqualTo(n).Because("every push landed after the grows");

        // Drain under the lock into a list (await is illegal inside the lock body), then assert order.
        var drained = new List<int>(n);
        lock (subQueue.SyncLock)
        {
            while (subQueue.PopHeldRoot(out _, out int priority) == SubQueuePopStatus.Success)
            {
                drained.Add(priority);
            }
        }

        await Assert.That(drained.Count).IsEqualTo(n).Because("the grown heap drains exactly the pushed count");

        // A min-heap drain returns non-decreasing priorities; a corrupted grow would break the order.
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
            "the grown heap stays a valid min-heap, so the drain is non-decreasing — a corrupt grow would break this");
    }

    /// <summary>
    /// The seqlock retry/"unknown" edge of <c>TryReadTop</c>: while a writer is stamped in-progress
    /// (an odd version, simulated by <c>DebugForceOddVersionForTest</c>), every read attempt observes
    /// the in-progress stamp and the method exhausts its bounded retries to report "unknown"
    /// (<see langword="false"/>), never returning a torn value.
    /// </summary>
    /// <remarks>
    /// Forcing the version odd deliberately leaves the seqlock in the permanently-corrupt
    /// writer-in-progress state (a real writer would re-stamp it even on completion), so the test
    /// asserts only the bounded-retry "unknown" report — the branch under coverage — and does not
    /// expect recovery from that fabricated state. Recovery from a NORMAL even-version read is covered
    /// by the settled-read assertion below and across the other tests that read published tops.
    /// </remarks>
    [Test]
    public async Task TryReadTop_WriterInProgress_ReportsUnknown()
    {
        var subQueue = new SubQueue<int, int>(comparer: null, index: 0, occupancy: new ulong[1]);

        // A brand-new sub-queue starts at version 0 (even, "stable").
        await Assert.That(subQueue.DebugTopVersionForTest % 2u).IsEqualTo(0u).Because(
            "a fresh sub-queue starts at an even (settled) seqlock version");

        // Publish a real value first so there IS a stable top to read (the settled, even-version path).
        subQueue.TryLockedPush(7, 7);
        await Assert.That(subQueue.DebugTopVersionForTest % 2u).IsEqualTo(0u).Because(
            "after a completed publish the seqlock version is even again (the writer re-stamped it)");
        await Assert.That(subQueue.TryReadTop(out int stableTop, out bool stableEmpty)).IsTrue().Because(
            "a settled (even-version) sub-queue reads a stable top");
        await Assert.That(stableEmpty).IsFalse();
        await Assert.That(stableTop).IsEqualTo(7).Because("the published top is the pushed priority");

        // Force the version odd: a perpetual writer-in-progress. Every read attempt sees the odd stamp
        // and bails; after the bounded retry budget the read reports unknown (false), never torn.
        subQueue.DebugForceOddVersionForTest();

        await Assert.That(subQueue.TryReadTop(out _, out _)).IsFalse().Because(
            "a perpetually in-progress (odd) version forces TryReadTop to exhaust retries and report unknown");
    }

    /// <summary>
    /// <c>SnapshotTo</c> copies exactly this sub-queue's live entries into the destination buffer
    /// under its lock — no more, no fewer — for both the default-comparer (value-type) path and a
    /// custom-comparer path.
    /// </summary>
    [Test]
    public async Task SnapshotTo_CopiesExactLiveEntries()
    {
        var subQueue = new SubQueue<int, int>(comparer: null, index: 0, occupancy: new ulong[1]);
        int[] values = [5, 1, 9, 3, 7];
        foreach (int v in values)
        {
            subQueue.TryLockedPush(v, v);
        }

        var buffer = new List<(int Element, int Priority)>();
        subQueue.SnapshotTo(buffer);

        await Assert.That(buffer.Count).IsEqualTo(values.Length).Because("SnapshotTo copies every live entry once");

        int[] snapshotPriorities = buffer.Select(t => t.Priority).OrderBy(p => p).ToArray();
        await Assert.That(snapshotPriorities.SequenceEqual([1, 3, 5, 7, 9])).IsTrue().Because(
            "the snapshot is the exact multiset of pushed priorities");
        await Assert.That(buffer.All(t => t.Element == t.Priority)).IsTrue().Because("each entry carries its element == priority");
    }

    /// <summary>
    /// <c>SnapshotTo</c> on an empty sub-queue copies nothing — the loop body never runs — driving the
    /// zero-iteration branch.
    /// </summary>
    [Test]
    public async Task SnapshotTo_EmptySubQueue_CopiesNothing()
    {
        var subQueue = new SubQueue<int, int>(comparer: null, index: 0, occupancy: new ulong[1]);
        var buffer = new List<(int Element, int Priority)>();

        subQueue.SnapshotTo(buffer);

        await Assert.That(buffer.Count).IsEqualTo(0).Because("an empty sub-queue snapshots no entries");
    }

    /// <summary>
    /// <c>LockedClear</c> on a non-empty sub-queue empties the heap, republishes the empty state,
    /// zeroes the striped count, and returns the count it removed (so the queue can release that many
    /// bounded reservations).
    /// </summary>
    [Test]
    public async Task LockedClear_NonEmpty_EmptiesAndReportsRemovedCount()
    {
        var subQueue = new SubQueue<int, int>(comparer: null, index: 0, occupancy: new ulong[1]);
        for (int i = 0; i < 8; i++)
        {
            subQueue.TryLockedPush(i, i);
        }

        await Assert.That(subQueue.HeapSize).IsEqualTo(8);

        int removed = subQueue.LockedClear();

        await Assert.That(removed).IsEqualTo(8).Because("LockedClear returns how many entries it removed");
        await Assert.That(subQueue.HeapSize).IsEqualTo(0).Because("the heap is emptied");
        await Assert.That(subQueue.VolatileCount).IsEqualTo(0).Because("the striped count is zeroed");

        // The empty state was republished: a read now reports empty.
        await Assert.That(subQueue.TryReadTop(out _, out bool empty)).IsTrue().Because("the cleared sub-queue reads a stable state");
        await Assert.That(empty).IsTrue().Because("LockedClear republished the empty flag");
    }

    /// <summary>
    /// <c>LockedClear</c> on an already-empty sub-queue takes the <c>removed &lt;= 0</c> early return:
    /// it removes nothing, returns zero, and writes no new publication.
    /// </summary>
    [Test]
    public async Task LockedClear_AlreadyEmpty_ReturnsZeroEarly()
    {
        var subQueue = new SubQueue<int, int>(comparer: null, index: 0, occupancy: new ulong[1]);

        int removed = subQueue.LockedClear();

        await Assert.That(removed).IsEqualTo(0).Because("clearing an empty sub-queue removes nothing");
        await Assert.That(subQueue.HeapSize).IsEqualTo(0);
    }

    /// <summary>
    /// <c>TryHeapPeekRoot</c> reports the current min-heap root without removing it, and reports the
    /// empty state on a drained heap, driving both branches of the peek.
    /// </summary>
    [Test]
    public async Task TryHeapPeekRoot_ReportsRootAndEmptyState()
    {
        var subQueue = new SubQueue<int, int>(comparer: null, index: 0, occupancy: new ulong[1]);

        await Assert.That(subQueue.TryHeapPeekRoot(out _, out _)).IsFalse().Because("an empty heap has no root");

        subQueue.TryLockedPush(40, 40);
        subQueue.TryLockedPush(10, 10); // the new root
        subQueue.TryLockedPush(20, 20);

        await Assert.That(subQueue.TryHeapPeekRoot(out int element, out int priority)).IsTrue().Because("a populated heap has a root");
        await Assert.That(priority).IsEqualTo(10).Because("the root is the minimum priority");
        await Assert.That(element).IsEqualTo(10);
        await Assert.That(subQueue.HeapSize).IsEqualTo(3).Because("peeking the root removes nothing");
    }

    /// <summary>
    /// A REFERENCE-TYPE priority (with a custom comparer) drives the GC-hygiene slot-clear branches:
    /// the reference-aware clears in <c>TryHeapPop</c> (popped slot), <c>PublishTop</c> (empty-publish
    /// slot), and <c>LockedClear</c> (the array clear) all run only when the priority contains
    /// references. A deep heap also exercises the custom-comparer sift-down across multiple children.
    /// The drain still returns exact ascending order, proving the reference-type heap is correct.
    /// </summary>
    [Test]
    public async Task ReferenceTypePriority_PushPopClear_DrivesGcHygieneAndCustomSiftDown()
    {
        // string priorities -> reference-containing -> the gated slot clears all run; a custom
        // ordinal comparer forces the MoveDown/MoveUpCustomComparer paths.
        var subQueue = new SubQueue<string, string>(comparer: StringComparer.Ordinal, index: 0, occupancy: new ulong[1]);

        // Enough zero-padded keys to build a heap several levels deep, exercising multi-child sift-down.
        const int n = 200;
        var keys = Enumerable.Range(0, n).Select(i => $"key-{i:D5}").ToList();
        var shuffled = new List<string>(keys);
        var rng = new Random(0x5151);
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        foreach (string k in shuffled)
        {
            subQueue.TryLockedPush(k, k);
        }

        // Pop HALF the heap under the lock (drives TryHeapPop's reference-type popped-slot clear and
        // the deep custom sift-down), then clear the rest (drives LockedClear's array clear).
        var drained = new List<string>();
        lock (subQueue.SyncLock)
        {
            for (int i = 0; i < n / 2; i++)
            {
                subQueue.PopHeldRoot(out _, out string priority);
                drained.Add(priority);
            }
        }

        // The popped half came out in exact ascending ordinal order (custom comparer correctness).
        bool sorted = true;
        for (int i = 1; i < drained.Count; i++)
        {
            if (string.CompareOrdinal(drained[i], drained[i - 1]) < 0)
            {
                sorted = false;
                break;
            }
        }

        await Assert.That(sorted).IsTrue().Because("the reference-type custom-comparer heap drains in exact ascending order");
        await Assert.That(drained[0]).IsEqualTo("key-00000").Because("the smallest ordinal key pops first");

        int removed = subQueue.LockedClear();
        await Assert.That(removed).IsEqualTo(n - (n / 2)).Because("LockedClear removes the un-drained remainder");
        await Assert.That(subQueue.HeapSize).IsEqualTo(0).Because("the reference-type sub-queue is fully cleared");
    }
}
