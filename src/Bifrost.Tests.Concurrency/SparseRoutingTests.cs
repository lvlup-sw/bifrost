// =============================================================================
// <copyright file="SparseRoutingTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Tests for the sparse-fallback routing phase of the relaxed dequeue (DR-3) and its staleness
/// safety (DR-4). After the two-choice sampling budget is spent without a pop, a bitmask-guided
/// routing phase reads the occupancy words and routes straight to a populated sub-queue via
/// <c>TrailingZeroCount</c> instead of running the O(n) verification scan. Routing is purely a hint:
/// it can only short-circuit to a successful pop or fall through to the scan, which stays the sole
/// authority for returning <see langword="false"/>.
/// </summary>
/// <remarks>
/// The tests drive the post-sampling path in isolation through the <c>TryDequeueRoutingOnlyForTest</c>
/// seam (skip Phase 1 sampling, run Phase 1.5 routing + Phase 2 scan) so the assertions are
/// deterministic — the production sampling phase is random and would only <i>probabilistically</i>
/// miss. Two instrumentation counters make the path observable: a routing-hit counter (Phase 1.5
/// popped) and a scan-entered counter (Phase 1.5 fell through to the O(n) scan).
/// </remarks>
public class SparseRoutingTests
{
    /// <summary>
    /// With one item in a single sub-queue and all others empty, the post-sampling path routes via
    /// the occupancy bitmask straight to that sub-queue and pops it — without entering the O(n)
    /// verification scan (DR-3).
    /// </summary>
    [Test]
    public async Task TryDequeue_OneItemAfterSamplingMiss_RoutesViaBitmaskWithoutFullScan()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 256, boundedCapacity: -1, comparer: null);
        const int targetIndex = 70; // word 1, bit 6 — also exercises multi-word routing.

        // One item, placed directly so the populated sub-queue is known and the occupancy bit set.
        await Assert.That(queue.SubQueuesForTest[targetIndex].TryLockedPush(element: 99, priority: 99)).IsTrue();

        long routingHitsBefore = queue.DebugRoutingHitCountForTest;
        long scanEntriesBefore = queue.DebugScanEntryCountForTest;

        // Drive the post-sampling path directly (sampling skipped) so routing is deterministically
        // exercised.
        bool popped = queue.TryDequeueRoutingOnlyForTest(out int element, out int priority);

        await Assert.That(popped).IsTrue().Because("routing finds the one populated sub-queue and pops it");
        await Assert.That(element).IsEqualTo(99);
        await Assert.That(priority).IsEqualTo(99);
        await Assert.That(queue.DebugRoutingHitCountForTest - routingHitsBefore).IsEqualTo(1L).Because(
            "Phase 1.5 routing popped the element");
        await Assert.That(queue.DebugScanEntryCountForTest - scanEntriesBefore).IsEqualTo(0L).Because(
            "routing succeeded, so the O(n) verification scan was never entered");
    }
}
