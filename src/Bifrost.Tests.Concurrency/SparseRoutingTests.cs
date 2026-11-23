// =============================================================================
// <copyright file="SparseRoutingTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Tests for the sparse routing step of the relaxed dequeue and its staleness safety. After the
/// two-choice sampling budget is spent without a pop, a bitmask-guided
/// routing step reads the occupancy words and routes straight to a populated sub-queue via
/// <c>TrailingZeroCount</c> instead of running the O(n) verification scan. Routing is only a hint:
/// it can short-circuit to a successful pop or fall through to the scan, which stays the one
/// authority allowed to return <see langword="false"/>.
/// </summary>
/// <remarks>
/// The tests drive the post-sampling path in isolation through the <c>TryDequeueRoutingOnlyForTest</c>
/// seam (skip sampling, run routing then the verification scan) so the assertions are
/// deterministic. The production sampling phase is random and would only <i>probabilistically</i>
/// miss. Two instrumentation counters make the path observable: a routing-hit counter (routing
/// popped) and a scan-entered counter (routing fell through to the O(n) scan).
/// </remarks>
public class SparseRoutingTests
{
    /// <summary>
    /// With one item in a single sub-queue and all others empty, the post-sampling path routes via
    /// the occupancy bitmask straight to that sub-queue and pops it, without entering the O(n)
    /// verification scan.
    /// </summary>
    /// <remarks>
    /// The target index 70 is word 1, bit 6, so the test also exercises multi-word routing. The test
    /// places the one item directly, so the populated sub-queue is known and its occupancy bit is set.
    /// <c>TryDequeueRoutingOnlyForTest</c> drives the post-sampling path directly and skips sampling,
    /// so routing is exercised deterministically.
    /// </remarks>
    [Test]
    public async Task TryDequeue_OneItemAfterSamplingMiss_RoutesViaBitmaskWithoutFullScan()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 256, boundedCapacity: -1, comparer: null);
        const int targetIndex = 70;
        await Assert.That(queue.SubQueuesForTest[targetIndex].TryLockedPush(element: 99, priority: 99)).IsTrue();

        long routingHitsBefore = queue.DebugRoutingHitCountForTest;
        long scanEntriesBefore = queue.DebugScanEntryCountForTest;
        bool popped = queue.TryDequeueRoutingOnlyForTest(out int element, out int priority);

        await Assert.That(popped).IsTrue().Because("routing finds the one populated sub-queue and pops it");
        await Assert.That(element).IsEqualTo(99);
        await Assert.That(priority).IsEqualTo(99);
        await Assert.That(queue.DebugRoutingHitCountForTest - routingHitsBefore).IsEqualTo(1L).Because(
            "routing popped the element");
        await Assert.That(queue.DebugScanEntryCountForTest - scanEntriesBefore).IsEqualTo(0L).Because(
            "routing succeeded, so the O(n) verification scan was never entered");
    }

    /// <summary>
    /// On a genuinely empty queue, the routing step finds no set bits and must <i>defer</i> to the
    /// verification scan, the one authority allowed to return <see langword="false"/>.
    /// Routing must never short-circuit to <see langword="false"/> on its own. The load-bearing
    /// assertion is that the scan was actually entered: a routing-returns-false implementation would
    /// leave the scan-entry counter at zero.
    /// </summary>
    /// <remarks>
    /// A fresh queue has all occupancy words at zero, and every sub-queue is empty.
    /// </remarks>
    [Test]
    public async Task TryDequeue_EmptyQueue_RoutingFindsNothing_VerificationScanReturnsFalse()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 256, boundedCapacity: -1, comparer: null);
        long routingHitsBefore = queue.DebugRoutingHitCountForTest;
        long scanEntriesBefore = queue.DebugScanEntryCountForTest;

        bool popped = queue.TryDequeueRoutingOnlyForTest(out _, out _);

        await Assert.That(popped).IsFalse().Because("a genuinely empty queue yields false");
        await Assert.That(queue.DebugRoutingHitCountForTest - routingHitsBefore).IsEqualTo(0L).Because(
            "no set bits, so routing popped nothing");
        await Assert.That(queue.DebugScanEntryCountForTest - scanEntriesBefore).IsEqualTo(1L).Because(
            "routing must defer the false verdict to the verification scan — the sole false authority");
        await Assert.That(queue.DebugLastFalseScanEmptyObservationsForTest).IsEqualTo(queue.SubQueueCountForTest).Because(
            "the false came from a full all-empty pass, preserving the observed-empty contract verbatim");
    }

    /// <summary>
    /// The full public <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryDequeue"/> (not the
    /// routing-only seam) returns <see langword="false"/> on an empty queue via the verification scan,
    /// confirming the production path's routing-to-scan fall-through is wired correctly.
    /// </summary>
    [Test]
    public async Task TryDequeue_PublicPath_EmptyQueue_FallsThroughToScanAndReturnsFalse()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 256, boundedCapacity: -1, comparer: null);

        long scanEntriesBefore = queue.DebugScanEntryCountForTest;
        bool popped = queue.TryDequeue(out _, out _);

        await Assert.That(popped).IsFalse();
        await Assert.That(queue.DebugScanEntryCountForTest - scanEntriesBefore).IsEqualTo(1L).Because(
            "the public TryDequeue reaches the scan on an empty queue (sampling and routing both miss)");
    }

    /// <summary>
    /// When the two-choice sampling phase lands a pop within budget (the dense fast path), the dequeue
    /// returns before routing, so the routing step is never entered. A densely-populated queue shows
    /// this: every sub-queue holds items, so the first sample round pops
    /// successfully and neither the routing-hit nor the scan-entry counter moves.
    /// </summary>
    /// <remarks>
    /// All 16 sub-queues are populated, so any sampled pair is non-empty and the first round pops. Each
    /// sub-queue holds 1000 items, and the test does only 2000 dequeues. Thus the queue stays dense
    /// throughout, and sampling always lands within budget.
    /// </remarks>
    [Test]
    public async Task TryDequeue_SamplingSucceeds_DoesNotEnterRoutingPhase()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 16, boundedCapacity: -1, comparer: null);
        for (int i = 0; i < queue.SubQueueCountForTest; i++)
        {
            for (int j = 0; j < 1000; j++)
            {
                await Assert.That(queue.SubQueuesForTest[i].TryLockedPush(element: (i * 1000) + j, priority: j)).IsTrue();
            }
        }

        long routingHitsBefore = queue.DebugRoutingHitCountForTest;
        long scanEntriesBefore = queue.DebugScanEntryCountForTest;
        for (int n = 0; n < 2000; n++)
        {
            await Assert.That(queue.TryDequeue(out _, out _)).IsTrue().Because("a dense queue always yields a pop");
        }

        await Assert.That(queue.DebugRoutingHitCountForTest - routingHitsBefore).IsEqualTo(0L).Because(
            "sampling succeeded every time, so routing was never entered on the dense path");
        await Assert.That(queue.DebugScanEntryCountForTest - scanEntriesBefore).IsEqualTo(0L).Because(
            "and the O(n) verification scan was never entered on the dense path either");
    }

    /// <summary>
    /// A stale-set bit (occupancy reads 1 over an actually-empty sub-queue) is asymmetric-safe:
    /// routing attempts the locked pop, gets <c>Empty</c>, skips the bit and
    /// continues, never trusting the bit as proof of an element. Over an otherwise-empty queue the
    /// stale-set bit must therefore resolve to a <see langword="false"/> from the verification scan,
    /// not a bogus <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <c>DebugForceSetOccupancyBitForTest</c> sets a bit over an empty sub-queue. This is a stale-set
    /// bit: no element backs it.
    /// </remarks>
    [Test]
    public async Task Routing_StaleSetBitOverEmptySubQueue_FallsThroughSafely()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 256, boundedCapacity: -1, comparer: null);
        const int staleIndex = 70;
        queue.DebugForceSetOccupancyBitForTest(staleIndex);
        await Assert.That(queue.DebugOccupancyForTest[1]).IsEqualTo(1UL << 6).Because("the stale bit is forced set");

        long scanEntriesBefore = queue.DebugScanEntryCountForTest;

        bool popped = queue.TryDequeueRoutingOnlyForTest(out _, out _);

        await Assert.That(popped).IsFalse().Because(
            "the stale-set bit backs no element — routing gets Empty, never returns a bogus true");
        await Assert.That(queue.DebugScanEntryCountForTest - scanEntriesBefore).IsEqualTo(1L).Because(
            "after the stale bit resolves to Empty, routing falls through to the scan, which returns false");
    }

    /// <summary>
    /// A stale-set bit at a lower index does not block routing from popping a genuinely populated
    /// sub-queue at a higher index: routing skips the stale (lowest-first) bit on
    /// <c>Empty</c> and continues to the real one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stale bit is at the lower index (10), so routing reaches it first and must skip it. The
    /// genuine item is at the higher index (200).
    /// </para>
    /// <para>
    /// The routing-hit and scan-entry counters prove that routing itself popped the real item. The scan
    /// would also find <c>realIndex</c>, so it must never be entered. Thus a routing-vs-scan regression
    /// cannot pass this test silently.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Routing_StaleSetBitBelowRealItem_SkipsStaleAndPopsReal()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 256, boundedCapacity: -1, comparer: null);

        const int staleIndex = 10;
        const int realIndex = 200;
        queue.DebugForceSetOccupancyBitForTest(staleIndex);
        await Assert.That(queue.SubQueuesForTest[realIndex].TryLockedPush(element: 7, priority: 7)).IsTrue();

        long routingHitsBefore = queue.DebugRoutingHitCountForTest;
        long scanEntriesBefore = queue.DebugScanEntryCountForTest;

        bool popped = queue.TryDequeueRoutingOnlyForTest(out int element, out int priority);

        await Assert.That(popped).IsTrue().Because("routing skips the stale-set bit and pops the real item");
        await Assert.That(element).IsEqualTo(7);
        await Assert.That(priority).IsEqualTo(7);
        await Assert.That(queue.DebugRoutingHitCountForTest - routingHitsBefore).IsEqualTo(1L).Because(
            "routing skipped the stale bit, continued to realIndex, and popped it");
        await Assert.That(queue.DebugScanEntryCountForTest - scanEntriesBefore).IsEqualTo(0L).Because(
            "routing found the real item, so the verification scan was never entered");
    }
}
