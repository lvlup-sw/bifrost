// =============================================================================
// <copyright file="EmptySemanticTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry.Tests/Concurrency/MultiQueue/EmptySemanticTests.cs)

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Stress tests for the observed-empty contract of <c>TryDequeue</c> (DR-9) and the post-drain
/// emptiness consistency surface (DR-17), exercising the authoritative
/// <c>TryDequeueVerificationScan</c> under concurrency.
/// </summary>
/// <remarks>
/// <para>
/// <b>The contract under test (DR-9).</b> <c>TryDequeue</c> may return <see langword="false"/> only
/// when "the queue was observed empty at some point during the call" — the verification scan must
/// complete one full pass seeing every sub-queue empty before it is allowed to conclude emptiness.
/// The naive forbidden implementation (a sampling miss returning instant <see langword="false"/>)
/// violates this: it can report empty while tens of thousands of elements still sit in unsampled
/// sub-queues.
/// </para>
/// <para>
/// <b>How the violation is made observable (the quiescent-drain technique).</b> A hard barrier
/// separates production from consumption: all enqueues complete single-threaded in phase 1 before
/// any consumer starts in phase 2. With no in-flight producer to excuse a stale observation, a
/// <see langword="false"/> from any consumer while the shared remaining-element count is still
/// strictly positive is an unambiguous contract violation — there is no enqueue that could have
/// completed after an honest all-empty observation window.
/// </para>
/// <para>
/// <b>Kill-probe (RED witness).</b> These tests were verified to DETECT the forbidden
/// implementation before being trusted to assert its absence. Temporarily replacing the phase-2
/// <c>TryDequeueVerificationScan</c> call in <c>ConcurrentPriorityQueue.Dequeue.cs</c> with an
/// instant <c>return false</c> (sampling misses become instant empties — the naive design the
/// contract forbids) makes test 1 fail with tens of thousands of recorded violations; with the
/// real scan restored it passes with zero. The mutation procedure is recorded in the task notes.
/// </para>
/// </remarks>
public class EmptySemanticTests
{
    private const int ElementCount = 50_000;
    private const int ConsumerCount = 6;

    /// <summary>
    /// The observed-empty proof (§5.2 / T17) on the default queue (<c>s = 1</c>): with production
    /// quiescent, no concurrent consumer may ever see <c>TryDequeue</c> return
    /// <see langword="false"/> while the queue's own <c>Count</c> is still positive (DR-9).
    /// </summary>
    [Test]
    public async Task TryDequeue_AfterAllEnqueuesComplete_NeverFalseWhileElementsRemain()
        => await AssertNoFalseEmptyWhileElementsRemain(new ConcurrentPriorityQueue<int, int>()).ConfigureAwait(false);

    /// <summary>
    /// The observed-empty proof (§5.2 / T17) re-run with stickiness enabled (<c>s = 4</c>): every
    /// consumer reuses a stuck sampled pair for several dequeues, so the quiescent concurrent drain
    /// drives the wait-free reset-on-contention/empty path (<c>ResetStickyDequeue</c>) under real
    /// multi-consumer contention. Stickiness changes only <i>which</i> sub-queue pair is sampled — it
    /// must never let the verification scan conclude emptiness while elements demonstrably remain (a
    /// stuck pair that kept re-sampling a drained selection could otherwise bypass the scan). The
    /// no-false-empty contract must hold identically to the default <c>s = 1</c> path.
    /// </summary>
    [Test]
    public async Task EmptySemantics_MultiThreaded_WithStickiness_NoFalseEmpty()
        => await AssertNoFalseEmptyWhileElementsRemain(
            new ConcurrentPriorityQueue<int, int>(boundedCapacity: -1, stickiness: 4)).ConfigureAwait(false);

    /// <summary>
    /// Reads the queue's <c>Count</c> and re-confirms it stays above <paramref name="inFlightTolerance"/>
    /// across a bounded spin, returning the persistently-observed count or <c>0</c> otherwise. This
    /// distinguishes a resident element from the benign transient where a concurrent consumer has
    /// published a sub-queue's seqlock empty flag but not yet written its striped <c>Count = 0</c> (two
    /// ordered writes under the held lock). Count over-reports by at most one per consumer mid-pop, so
    /// a Count at or below the consumer count could be entirely in-flight removals, however long a
    /// preempted consumer stalls; keying the threshold to the consumer count makes the witness sound
    /// regardless of scheduling, where a fixed-iteration wait could not.
    /// </summary>
    /// <param name="queue">The queue to probe.</param>
    /// <param name="inFlightTolerance">Maximum Count attributable to concurrent in-flight removals.</param>
    /// <returns>The persistently-observed count above the tolerance, or <c>0</c> otherwise.</returns>
    private static int PersistentNonEmptyCount(ConcurrentPriorityQueue<int, int> queue, int inFlightTolerance)
    {
        int observed = queue.Count;
        if (observed <= inFlightTolerance)
        {
            return 0;
        }

        var spinner = new SpinWait();
        for (int i = 0; i < 64; i++)
        {
            int reread = queue.Count;
            if (reread <= inFlightTolerance)
            {
                return 0;
            }

            observed = reread;
            spinner.SpinOnce();
        }

        return observed;
    }

    /// <summary>
    /// Shared body of the quiescent-drain observed-empty proof (DR-9), parameterized only by the
    /// supplied <paramref name="queue"/> so the exact same single-threaded-fill / concurrent-drain
    /// race runs against both the default (<c>s = 1</c>) and a stickiness-enabled queue. A
    /// <see langword="false"/> from any consumer while the queue's own <c>Count</c> is still positive
    /// — with production quiescent — is an unambiguous DR-9 violation regardless of the stickiness dial.
    /// </summary>
    /// <param name="queue">The queue under test (default or stickiness-configured).</param>
    private static async Task AssertNoFalseEmptyWhileElementsRemain(ConcurrentPriorityQueue<int, int> queue)
    {
        // Arrange — PHASE 1: enqueue every element single-threaded and let it FULLY complete before
        // any consumer runs. This hard barrier is the heart of the test: once production is done
        // there is no in-flight producer that could excuse a stale "observed empty" window, so a
        // false-while-non-empty has no honest explanation.
        for (int i = 0; i < ElementCount; i++)
        {
            queue.Enqueue(i, i);
        }

        // Shared drain accounting: `remaining` starts at the full element count and is decremented
        // once per successful pop. It serves ONLY as the drain-termination signal and the
        // conservation tally — it is NOT the violation witness, because the decrement happens
        // *after* TryDequeue returns, so `remaining` lags the real pop by the count of in-flight
        // (popped-but-not-yet-decremented) elements. Using that lagging external counter as the
        // witness produces spurious "violations" of magnitude ≤ ConsumerCount when a consumer reads
        // a false right as another pops the last element but has not yet decremented.
        long remaining = ElementCount;
        long totalPopped = 0;
        long violations = 0;
        long firstViolationCount = -1;
        object violationGate = new();

        // PHASE 2: six consumers drain concurrently. A consumer exits when the queue is drained
        // (remaining == 0) OR the instant it witnesses an unambiguous violation, so a broken
        // implementation fails fast instead of spinning.
        var consumers = new Thread[ConsumerCount];
        for (int c = 0; c < ConsumerCount; c++)
        {
            consumers[c] = new Thread(() =>
            {
                long localPopped = 0;

                while (Volatile.Read(ref remaining) > 0)
                {
                    if (queue.TryDequeue(out _, out _))
                    {
                        Interlocked.Decrement(ref remaining);
                        localPopped++;
                        continue;
                    }

                    // A false was returned. The violation witness is the queue's OWN Count read
                    // AFTER the false (the check order is load-bearing). Count is the authoritative
                    // measure of elements still present: SubQueue.TryLockedPop decrements the
                    // striped count under the sub-queue lock before releasing it. With production
                    // quiescent the per-stripe counts only ever decrease.
                    //
                    // False is legal only when the queue was observed empty at some point during the
                    // call. A false produced while another consumer was concurrently popping the very
                    // last element is in-contract: the queue reached empty. The witness must also be
                    // sound against a benign transient. A consumer mid-pop publishes its sub-queue's
                    // seqlock empty flag BEFORE it writes the striped Count = 0 (two ordered writes
                    // under the held lock), so the scan's lock-free cheap route can legitimately
                    // observe "empty" while Count still counts that being-removed element. Up to
                    // ConsumerCount consumers can be mid-pop at once, so Count over-reports by at most
                    // ConsumerCount; only a Count that stays ABOVE that tolerance across a recheck spin
                    // (PersistentNonEmptyCount) proves a genuinely resident element, and that bound
                    // holds however long a preempted consumer stalls. A persistent such Count with
                    // production quiescent means the scan concluded emptiness while an element was
                    // resident and unremoved: the observed-empty violation. (Reading before the false
                    // would flag the legal last-element race; reading after, and requiring persistence,
                    // is what makes a positive Count load-bearing.)
                    int observedCount = PersistentNonEmptyCount(queue, inFlightTolerance: ConsumerCount);
                    if (observedCount > 0)
                    {
                        lock (violationGate)
                        {
                            if (firstViolationCount < 0)
                            {
                                firstViolationCount = observedCount;
                            }
                        }

                        Interlocked.Increment(ref violations);

                        // Exit on the first witnessed violation: the contract is already broken, no
                        // need to keep draining.
                        break;
                    }
                }

                Interlocked.Add(ref totalPopped, localPopped);
            })
            { IsBackground = true, Name = $"empty-drain-consumer-{c}" };
        }

        // Act — start all consumers and join.
        foreach (var consumer in consumers)
        {
            consumer.Start();
        }

        foreach (var consumer in consumers)
        {
            consumer.Join();
        }

        // Assert — zero contract violations, and conservation: every enqueued element was popped
        // exactly once (the drain is complete only if no consumer bailed out on a violation).
        await Assert.That(violations).IsEqualTo(0L).Because(
            "TryDequeue returned false while the queue's own Count was still positive and no " +
            $"producer was in flight — a DR-9 observed-empty violation; first violation saw Count={firstViolationCount:N0}");
        await Assert.That(totalPopped).IsEqualTo((long)ElementCount).Because(
            "the quiescent drain must remove every element exactly once (conservation)");
        await Assert.That(queue.IsEmpty).IsTrue().Because("the queue must be empty after a full drain");
    }

    /// <summary>
    /// The post-drain emptiness consistency surface (DR-17): after a true drain-to-empty the three
    /// emptiness surfaces agree (<c>IsEmpty</c> true, <c>Count</c> zero, a quiescent
    /// <c>TryDequeue</c> false), and the queue stays fully functional for a subsequent enqueue.
    /// </summary>
    [Test]
    public async Task IsEmpty_AfterTrueDrain_ConsistentWithTryDequeueFalse()
    {
        // Arrange — enqueue then drain the queue completely (a true drain-to-empty, the same
        // single-threaded-fill / concurrent-drain shape as test 1).
        var queue = new ConcurrentPriorityQueue<int, int>();
        for (int i = 0; i < ElementCount; i++)
        {
            queue.Enqueue(i, i);
        }

        long remaining = ElementCount;
        var consumers = new Thread[ConsumerCount];
        for (int c = 0; c < ConsumerCount; c++)
        {
            consumers[c] = new Thread(() =>
            {
                while (Volatile.Read(ref remaining) > 0)
                {
                    if (queue.TryDequeue(out _, out _))
                    {
                        Interlocked.Decrement(ref remaining);
                    }
                }
            })
            { IsBackground = true, Name = $"empty-consistency-consumer-{c}" };
        }

        foreach (var consumer in consumers)
        {
            consumer.Start();
        }

        foreach (var consumer in consumers)
        {
            consumer.Join();
        }

        // Assert — after a true drain the three emptiness surfaces agree: IsEmpty is true, Count is
        // zero, and a final (now single-threaded, quiescent) TryDequeue returns false.
        await Assert.That(queue.IsEmpty).IsTrue().Because("a fully drained queue reports IsEmpty == true");
        await Assert.That(queue.Count).IsEqualTo(0).Because("a fully drained queue reports Count == 0");
        await Assert.That(queue.TryDequeue(out _, out _)).IsFalse().Because(
            "a final TryDequeue on a quiescent, fully drained queue must return false");

        // The queue remains fully functional after a drain-to-empty: one Enqueue flips IsEmpty back
        // to false and the element is retrievable.
        queue.Enqueue(element: 7, priority: 7);
        await Assert.That(queue.IsEmpty).IsFalse().Because("a post-drain Enqueue flips IsEmpty back to false");
        await Assert.That(queue.TryDequeue(out int element, out int priority)).IsTrue().Because(
            "the queue stays functional after a drain-to-empty — the re-enqueued element is retrievable");
        await Assert.That(element).IsEqualTo(7);
        await Assert.That(priority).IsEqualTo(7);
    }
}
