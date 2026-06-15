// =============================================================================
// <copyright file="OccupancyBitmaskTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Unit tests for the per-instance occupancy bitmask and its boundary-only transition writes. They
/// cover the indexing math (<c>word</c>/<c>bit</c>), the array sizing with unused-high-bit masking
/// at a non-multiple-of-64 sub-queue count, the fresh-queue all-zero invariant, and that the bit is
/// set and cleared on the empty&#8596;non-empty crossings and never on a non-boundary mutation.
/// </summary>
/// <remarks>
/// The bitmask is a global occupancy index: bit <c>i</c> is set when sub-queue <c>i</c>
/// publishes itself non-empty. It speeds up the sparse routing step of the relaxed dequeue while
/// staying dormant on the dense hot path. These tests pin its invariants
/// through the internal test seams (<c>DebugOccupancyForTest</c>,
/// <c>DebugOccupancyWriteCountForTest</c>) rather than inferring them through the public surface.
/// </remarks>
public class OccupancyBitmaskTests
{
    /// <summary>
    /// The indexing helpers map sub-queue index <c>i</c> to word <c>i &gt;&gt; 6</c> and bit
    /// <c>1UL &lt;&lt; (i &amp; 63)</c> (64 bits per word, no division), mirroring the
    /// <c>_subQueueMask</c> shift/mask discipline.
    /// </summary>
    [Test]
    public async Task OccupancyBitmask_WordAndBit_MapIndexCorrectly()
    {
        // Word index is i / 64 expressed as a shift.
        await Assert.That(ConcurrentPriorityQueue<int, int>.OccupancyWordForTest(0)).IsEqualTo(0);
        await Assert.That(ConcurrentPriorityQueue<int, int>.OccupancyWordForTest(63)).IsEqualTo(0);
        await Assert.That(ConcurrentPriorityQueue<int, int>.OccupancyWordForTest(64)).IsEqualTo(1);
        await Assert.That(ConcurrentPriorityQueue<int, int>.OccupancyWordForTest(127)).IsEqualTo(1);
        await Assert.That(ConcurrentPriorityQueue<int, int>.OccupancyWordForTest(128)).IsEqualTo(2);
        await Assert.That(ConcurrentPriorityQueue<int, int>.OccupancyWordForTest(255)).IsEqualTo(3);

        // Bit mask is a single set bit at (i mod 64).
        await Assert.That(ConcurrentPriorityQueue<int, int>.OccupancyBitForTest(0)).IsEqualTo(1UL);
        await Assert.That(ConcurrentPriorityQueue<int, int>.OccupancyBitForTest(1)).IsEqualTo(2UL);
        await Assert.That(ConcurrentPriorityQueue<int, int>.OccupancyBitForTest(63)).IsEqualTo(1UL << 63);
        await Assert.That(ConcurrentPriorityQueue<int, int>.OccupancyBitForTest(64)).IsEqualTo(1UL);
        await Assert.That(ConcurrentPriorityQueue<int, int>.OccupancyBitForTest(65)).IsEqualTo(2UL);
        await Assert.That(ConcurrentPriorityQueue<int, int>.OccupancyBitForTest(127)).IsEqualTo(1UL << 63);
    }

    /// <summary>
    /// The bitmask is sized to <c>ceil(n / 64)</c> words. For a non-multiple-of-64 sub-queue count
    /// (e.g. 32) the single word's unused high bits stay zero and are never interpreted as occupied.
    /// The exact-collapse case (<c>n = 1</c>) is a single word as well.
    /// </summary>
    [Test]
    public async Task OccupancyBitmask_SizedForSubQueueCount_MasksUnusedHighBits()
    {
        // n = 32: one word; on a fresh queue every (live and unused) bit is zero.
        var q32 = new ConcurrentPriorityQueue<int, int>(subQueueCount: 32, boundedCapacity: -1, comparer: null);
        ulong[] occ32 = q32.DebugOccupancyForTest;
        await Assert.That(occ32.Length).IsEqualTo(1).Because("ceil(32/64) == 1 word");
        await Assert.That(occ32[0]).IsEqualTo(0UL).Because("a fresh queue's unused high bits must be zero, never occupied");

        // n = 1 (exact collapse): still one word.
        var q1 = new ConcurrentPriorityQueue<int, int>(subQueueCount: 1, boundedCapacity: -1, comparer: null);
        await Assert.That(q1.DebugOccupancyForTest.Length).IsEqualTo(1).Because("ceil(1/64) == 1 word");

        // n = 64: exactly one word (boundary). n = 65 would round up to 128 sub-queues internally
        // (power-of-two), giving two words; n = 256 gives four words.
        var q64 = new ConcurrentPriorityQueue<int, int>(subQueueCount: 64, boundedCapacity: -1, comparer: null);
        await Assert.That(q64.DebugOccupancyForTest.Length).IsEqualTo(1).Because("ceil(64/64) == 1 word");

        var q256 = new ConcurrentPriorityQueue<int, int>(subQueueCount: 256, boundedCapacity: -1, comparer: null);
        await Assert.That(q256.DebugOccupancyForTest.Length).IsEqualTo(4).Because("ceil(256/64) == 4 words");
    }

    /// <summary>
    /// A freshly constructed queue's occupancy bitmask is all zero in every word: every sub-queue
    /// starts empty, mirroring each <c>SubQueue</c>'s initial <c>EmptyFlag = 1</c>.
    /// </summary>
    [Test]
    public async Task OccupancyBitmask_FreshQueue_AllWordsZero()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 256, boundedCapacity: -1, comparer: null);

        ulong[] occupancy = queue.DebugOccupancyForTest;

        await Assert.That(occupancy.Length).IsEqualTo(4).Because("256 sub-queues == ceil(256/64) == 4 words");
        foreach (ulong word in occupancy)
        {
            await Assert.That(word).IsEqualTo(0UL).Because("no sub-queue has published non-empty on a fresh queue");
        }
    }

    /// <summary>
    /// Pushing the first item into an empty sub-queue (the empty&#8594;non-empty crossing) sets that
    /// sub-queue's occupancy bit, and only that bit. The bit lives in word <c>i &gt;&gt; 6</c> at
    /// position <c>i &amp; 63</c>. The chosen sub-queue index lands in word 1, so the test also
    /// exercises the multi-word addressing.
    /// </summary>
    [Test]
    public async Task TryLockedPush_FirstItemIntoEmptySubQueue_SetsOccupancyBit()
    {
        // 256 sub-queues → 4 occupancy words. Drive a specific sub-queue directly so the assertion
        // is deterministic (the public Enqueue scatters across sub-queues).
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 256, boundedCapacity: -1, comparer: null);
        const int targetIndex = 70; // word 1 (70 >> 6 == 1), bit 6 (70 & 63 == 6).

        bool pushed = queue.SubQueuesForTest[targetIndex].TryLockedPush(element: 42, priority: 42);
        await Assert.That(pushed).IsTrue().Because("an uncontended push into a fresh sub-queue succeeds");

        ulong[] occupancy = queue.DebugOccupancyForTest;
        await Assert.That(occupancy[1]).IsEqualTo(1UL << 6).Because("the first push sets exactly sub-queue 70's bit");
        await Assert.That(occupancy[0]).IsEqualTo(0UL).Because("no other word is touched");
        await Assert.That(occupancy[2]).IsEqualTo(0UL).Because("no other word is touched");
        await Assert.That(occupancy[3]).IsEqualTo(0UL).Because("no other word is touched");
    }

    /// <summary>
    /// Popping the last item from a sub-queue (the non-empty&#8594;empty crossing) clears its
    /// occupancy bit. The pop funnels through <c>PopHeldRoot</c>, so this also covers
    /// <c>TryLockedPop</c>.
    /// </summary>
    [Test]
    public async Task PopHeldRoot_LastItemRemoved_ClearsOccupancyBit()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 256, boundedCapacity: -1, comparer: null);
        const int targetIndex = 70; // word 1, bit 6.
        SubQueue<int, int> sub = queue.SubQueuesForTest[targetIndex];

        // Push two, pop one (bit must STAY set, since a non-last pop is not a boundary crossing),
        // then pop the last (bit must clear).
        await Assert.That(sub.TryLockedPush(element: 1, priority: 1)).IsTrue();
        await Assert.That(sub.TryLockedPush(element: 2, priority: 2)).IsTrue();
        await Assert.That(queue.DebugOccupancyForTest[1]).IsEqualTo(1UL << 6).Because("two items in: bit set");

        SubQueuePopStatus first = sub.TryLockedPop(out _, out _);
        await Assert.That(first).IsEqualTo(SubQueuePopStatus.Success);
        await Assert.That(queue.DebugOccupancyForTest[1]).IsEqualTo(1UL << 6).Because(
            "a non-last pop leaves an entry behind — not a boundary crossing, bit stays set");

        SubQueuePopStatus second = sub.TryLockedPop(out _, out _);
        await Assert.That(second).IsEqualTo(SubQueuePopStatus.Success);
        await Assert.That(queue.DebugOccupancyForTest[1]).IsEqualTo(0UL).Because(
            "the last pop drains the sub-queue — the non-empty→empty crossing clears the bit");
    }

    /// <summary>
    /// Clearing a non-empty sub-queue via <c>LockedClear</c> (the <c>Clear()</c> path) clears its
    /// occupancy bit alongside the empty publish.
    /// </summary>
    [Test]
    public async Task LockedClear_NonEmptySubQueue_ClearsOccupancyBit()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 256, boundedCapacity: -1, comparer: null);
        const int targetIndex = 70; // word 1, bit 6.
        SubQueue<int, int> sub = queue.SubQueuesForTest[targetIndex];

        await Assert.That(sub.TryLockedPush(element: 1, priority: 1)).IsTrue();
        await Assert.That(sub.TryLockedPush(element: 2, priority: 2)).IsTrue();
        await Assert.That(queue.DebugOccupancyForTest[1]).IsEqualTo(1UL << 6).Because("two items in: bit set");

        int removed = sub.LockedClear();
        await Assert.That(removed).IsEqualTo(2).Because("LockedClear reports the entries it removed");
        await Assert.That(queue.DebugOccupancyForTest[1]).IsEqualTo(0UL).Because(
            "LockedClear empties the sub-queue and must clear its occupancy bit");
    }

    /// <summary>
    /// Only boundary crossings write the bitmask: a push onto an already-populated sub-queue
    /// and a pop that leaves entries behind perform no <c>Interlocked</c> write to the occupancy
    /// word. The instrumented per-sub-queue transition-write counter verifies this. That counter is
    /// the mechanism that keeps the bitmask dormant on the dense hot path.
    /// </summary>
    [Test]
    public async Task Occupancy_PushToPopulatedAndNonLastPop_PerformsNoBitmaskWrite()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 256, boundedCapacity: -1, comparer: null);
        const int targetIndex = 70;
        SubQueue<int, int> sub = queue.SubQueuesForTest[targetIndex];

        // First push is a boundary crossing: one write (the set).
        await Assert.That(sub.TryLockedPush(element: 5, priority: 5)).IsTrue();
        await Assert.That(sub.DebugOccupancyWriteCountForTest).IsEqualTo(1L).Because(
            "the empty→non-empty push is the only boundary crossing so far");

        // Subsequent pushes onto the populated sub-queue are NOT boundary crossings: counter frozen.
        await Assert.That(sub.TryLockedPush(element: 6, priority: 6)).IsTrue();
        await Assert.That(sub.TryLockedPush(element: 7, priority: 7)).IsTrue();
        await Assert.That(sub.DebugOccupancyWriteCountForTest).IsEqualTo(1L).Because(
            "pushes onto a populated sub-queue must not touch the bitmask");

        // Non-last pops (3 in → pop 2, leaving 1) are NOT boundary crossings: counter frozen.
        await Assert.That(sub.TryLockedPop(out _, out _)).IsEqualTo(SubQueuePopStatus.Success);
        await Assert.That(sub.TryLockedPop(out _, out _)).IsEqualTo(SubQueuePopStatus.Success);
        await Assert.That(sub.DebugOccupancyWriteCountForTest).IsEqualTo(1L).Because(
            "non-last pops leave entries behind and must not touch the bitmask");

        // The last pop IS a boundary crossing: one more write (the clear).
        await Assert.That(sub.TryLockedPop(out _, out _)).IsEqualTo(SubQueuePopStatus.Success);
        await Assert.That(sub.DebugOccupancyWriteCountForTest).IsEqualTo(2L).Because(
            "the non-empty→empty last pop is the second and only other boundary crossing");
    }

    /// <summary>
    /// The <c>n = 1</c> collapse behaves the same as today. The single-word bitmask's one
    /// bit mirrors the sole sub-queue's <c>EmptyFlag</c>, the relaxed dequeue returns the exact
    /// minimum (no relaxation when there is only one sub-queue), and a drained queue reports honest
    /// emptiness. The bitmask is additive: it must not perturb the collapsed exact-ordering case.
    /// </summary>
    [Test]
    public async Task TryDequeue_SingleSubQueue_ReturnsExactMinAndHonestEmpty()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 1, boundedCapacity: -1, comparer: null);
        await Assert.That(queue.DebugOccupancyForTest.Length).IsEqualTo(1).Because("n = 1 → one occupancy word");
        await Assert.That(queue.DebugOccupancyForTest[0]).IsEqualTo(0UL).Because("fresh single sub-queue: bit clear");

        // Enqueue out of priority order; with one sub-queue the dequeue is exact (the true minimum).
        queue.Enqueue(element: 30, priority: 30);
        queue.Enqueue(element: 10, priority: 10);
        queue.Enqueue(element: 20, priority: 20);
        await Assert.That(queue.DebugOccupancyForTest[0]).IsEqualTo(1UL).Because(
            "the single sub-queue is non-empty, so its one bit mirrors EmptyFlag = 0");

        await Assert.That(queue.TryDequeue(out int e1, out int p1)).IsTrue();
        await Assert.That(p1).IsEqualTo(10).Because("a single sub-queue yields the exact minimum");
        await Assert.That(e1).IsEqualTo(10);

        await Assert.That(queue.TryDequeue(out _, out int p2)).IsTrue();
        await Assert.That(p2).IsEqualTo(20).Because("exact ordering continues");

        await Assert.That(queue.DebugOccupancyForTest[0]).IsEqualTo(1UL).Because("one item left: bit still set");

        await Assert.That(queue.TryDequeue(out _, out int p3)).IsTrue();
        await Assert.That(p3).IsEqualTo(30);

        // Drained: the single bit mirrors EmptyFlag = 1 again, and TryDequeue is honestly empty.
        await Assert.That(queue.DebugOccupancyForTest[0]).IsEqualTo(0UL).Because(
            "the drained single sub-queue's bit clears, mirroring EmptyFlag = 1");
        await Assert.That(queue.TryDequeue(out _, out _)).IsFalse().Because("a drained queue is honestly empty");
        await Assert.That(queue.IsEmpty).IsTrue();
    }

    /// <summary>
    /// DR-3 (T8): with buffering active the occupancy bit tracks the <i>deletion buffer</i> <c>D</c>'s
    /// 0&#8596;non-0 boundary (replacing the heap 0&#8596;1 boundary). A buffered sub-queue's bit is set
    /// while <c>D</c> is non-empty — even when every element lives in the buffers and the heap is empty —
    /// and clears only once <c>D</c> drains to empty. The sparse-routing path therefore still finds a
    /// buffered sub-queue whose population is entirely buffer-resident.
    /// </summary>
    [Test]
    public async Task Occupancy_Buffered_ReflectsDeletionBufferState()
    {
        // A single buffered sub-queue: the occupancy word collapses to one bit, so the assertions read
        // word 0 directly. Buffering 16 with a tiny population keeps everything in D (heap stays empty).
        var queue = new ConcurrentPriorityQueue<int, int>(boundedCapacity: -1, stickiness: 1, bufferCapacity: 16);
        SubQueue<int, int> sub = queue.SubQueuesForTest[0];

        await Assert.That(queue.DebugOccupancyForTest[0]).IsEqualTo(0UL).Because("a fresh buffered sub-queue is empty");

        // First push seeds D (heap untouched): D non-empty => bit set.
        await Assert.That(sub.TryLockedPush(element: 5, priority: 5)).IsTrue();
        await Assert.That(sub.DeletionCountForTest).IsGreaterThan(0).Because("the push routed into D");
        await Assert.That(sub.HeapSize).IsEqualTo(0).Because("a tiny buffered push never touches the heap");
        await Assert.That(queue.DebugOccupancyForTest[0]).IsEqualTo(1UL).Because(
            "the bit is set because D is non-empty, even though the heap is empty (D-boundary occupancy)");

        // A second (non-front) push keeps D non-empty: still a single boundary write total.
        await Assert.That(sub.TryLockedPush(element: 9, priority: 9)).IsTrue();
        await Assert.That(queue.DebugOccupancyForTest[0]).IsEqualTo(1UL).Because("D stays non-empty, the bit stays set");
        await Assert.That(sub.DebugOccupancyWriteCountForTest).IsEqualTo(1L).Because(
            "only the empty->non-empty crossing wrote the bit; a push onto a non-empty D is not a boundary");

        // Pop one (D still non-empty): bit stays set, no boundary write.
        lock (sub.SyncLock)
        {
            sub.PopHeldRoot(out _, out _);
        }

        await Assert.That(queue.DebugOccupancyForTest[0]).IsEqualTo(1UL).Because("D is still non-empty after a non-last pop");

        // Pop the last (D drains to empty): the non-empty->empty crossing clears the bit.
        lock (sub.SyncLock)
        {
            sub.PopHeldRoot(out _, out _);
        }

        await Assert.That(sub.DeletionCountForTest).IsEqualTo(0).Because("the last pop drained D");
        await Assert.That(queue.DebugOccupancyForTest[0]).IsEqualTo(0UL).Because(
            "the bit clears on the D non-empty->empty crossing");
        await Assert.That(sub.DebugOccupancyWriteCountForTest).IsEqualTo(2L).Because(
            "exactly two boundary writes occurred: one set (first push) and one clear (final drain)");
    }
}
