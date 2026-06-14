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

    /// <summary>
    /// On a genuinely empty queue, the routing phase finds no set bits and must <i>defer</i> to the
    /// verification scan, which is the sole authority for returning <see langword="false"/> (DR-3).
    /// Routing must never short-circuit to <see langword="false"/> on its own. The load-bearing
    /// assertion is that the scan was actually entered — a routing-returns-false implementation would
    /// leave the scan-entry counter at zero.
    /// </summary>
    [Test]
    public async Task TryDequeue_EmptyQueue_RoutingFindsNothing_VerificationScanReturnsFalse()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 256, boundedCapacity: -1, comparer: null);

        // Fresh queue: occupancy all zero, every sub-queue empty.
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
    /// confirming the production path's Phase 1.5 → Phase 2 fall-through is wired correctly (DR-3).
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
}
