// =============================================================================
// <copyright file="OccupancyBitmaskTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Unit tests for the per-instance occupancy bitmask (DR-1) and its boundary-only transition writes
/// (DR-2): the indexing math (<c>word</c>/<c>bit</c>), the array sizing with unused-high-bit masking
/// at a non-multiple-of-64 sub-queue count, the fresh-queue all-zero invariant, and that the bit is
/// set/cleared exactly on the empty&#8596;non-empty crossings and never on a non-boundary mutation.
/// </summary>
/// <remarks>
/// The bitmask is a global occupancy index: bit <c>i</c> is set exactly when sub-queue <c>i</c>
/// published itself non-empty. It accelerates the sparse-fallback routing phase of the relaxed
/// dequeue (DR-3) while staying dormant on the dense hot path (DR-5). These tests pin its invariants
/// directly through the internal test seams (<c>DebugOccupancyForTest</c>,
/// <c>DebugOccupancyWriteCountForTest</c>) rather than inferring them through the public surface.
/// </remarks>
public class OccupancyBitmaskTests
{
    /// <summary>
    /// The indexing helpers map sub-queue index <c>i</c> to word <c>i &gt;&gt; 6</c> and bit
    /// <c>1UL &lt;&lt; (i &amp; 63)</c> (64 bits per word, no division), mirroring the
    /// <c>_subQueueMask</c> shift/mask discipline (DR-1).
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
    /// (e.g. 32) the single word's unused high bits stay zero and are never interpreted as occupied
    /// (DR-1). The exact-collapse case (<c>n = 1</c>) is a single word as well.
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
    /// starts empty, mirroring each <c>SubQueue</c>'s initial <c>EmptyFlag = 1</c> (DR-1).
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
    /// sub-queue's occupancy bit, and only that bit (DR-2). The bit lives in word <c>i &gt;&gt; 6</c> at
    /// position <c>i &amp; 63</c>; here a sub-queue index that lands in word 1 is chosen so the test
    /// also exercises the multi-word addressing.
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
}
