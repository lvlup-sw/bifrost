// =============================================================================
// <copyright file="BoundedCapacityEdgeTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Coverage for the bounded-capacity edges (DR-4) across
/// <c>ConcurrentPriorityQueue.cs</c> and <c>ConcurrentPriorityQueue.Enqueue.cs</c>: internal-ctor
/// <c>boundedCapacity</c> validation (reject <c>0</c> and any value below the <c>-1</c> unbounded
/// sentinel), the <see cref="InvalidOperationException"/> thrown by <c>Enqueue</c> on a full bounded
/// queue, the reservation <i>rollback</i> <c>catch</c> when a push throws (#18 — driven by a throwing
/// comparer), and <c>_boundedCount</c> conservation across a rejection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non-tautology.</b> The rollback test asserts that the bounded gate (<c>DebugBoundedCountForTest</c>)
/// returns to its pre-throw value and that subsequent capacity is actually reusable — a leaked
/// reservation would permanently shrink the usable bound, which the follow-up enqueues would expose.
/// The validation tests assert the precise exception type and that the rejected value never
/// constructs an instance.
/// </para>
/// </remarks>
public class BoundedCapacityEdgeTests
{
    /// <summary>
    /// The internal core constructor rejects a <c>boundedCapacity</c> of <c>0</c> with
    /// <see cref="ArgumentOutOfRangeException"/> — the funnel-invariant validation
    /// (<c>ValidateBoundedCapacityOrUnbounded</c>) runs even on the internal path, so a contradictory
    /// zero bound can never construct an instance.
    /// </summary>
    [Test]
    public async Task InternalCtor_ZeroBoundedCapacity_Throws()
    {
        await Assert.That(() => new ConcurrentPriorityQueue<int, int>(subQueueCount: 4, boundedCapacity: 0, comparer: null))
            .Throws<ArgumentOutOfRangeException>().Because("a bounded capacity of zero is contradictory and must be rejected");
    }

    /// <summary>
    /// The internal core constructor rejects a <c>boundedCapacity</c> below the <c>-1</c> unbounded
    /// sentinel (e.g. <c>-2</c>) with <see cref="ArgumentOutOfRangeException"/>: only <c>-1</c> means
    /// "unbounded", every other negative is invalid.
    /// </summary>
    [Test]
    public async Task InternalCtor_BelowMinusOneBoundedCapacity_Throws()
    {
        await Assert.That(() => new ConcurrentPriorityQueue<int, int>(subQueueCount: 4, boundedCapacity: -2, comparer: null))
            .Throws<ArgumentOutOfRangeException>().Because("only -1 is the unbounded sentinel; -2 and below are invalid");
    }

    /// <summary>
    /// The internal core constructor accepts exactly <c>-1</c> (unbounded) and a positive bound, and
    /// surfaces them through <see cref="ConcurrentPriorityQueue{TElement, TPriority}.BoundedCapacity"/>
    /// — the accept side of the validation branch.
    /// </summary>
    [Test]
    public async Task InternalCtor_ValidBounds_Construct()
    {
        var unbounded = new ConcurrentPriorityQueue<int, int>(subQueueCount: 4, boundedCapacity: -1, comparer: null);
        var bounded = new ConcurrentPriorityQueue<int, int>(subQueueCount: 4, boundedCapacity: 10, comparer: null);

        await Assert.That(unbounded.BoundedCapacity).IsEqualTo(-1).Because("the -1 sentinel reports unbounded");
        await Assert.That(bounded.BoundedCapacity).IsEqualTo(10).Because("a positive bound is honored");
    }

    /// <summary>
    /// The public bounded constructor (<c>ConcurrentPriorityQueue(boundedCapacity, comparer)</c>)
    /// rejects a non-positive bound through <c>ValidateBoundedCapacity</c>, exercising the public
    /// validation path distinct from the internal one.
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(-5)]
    public async Task PublicBoundedCtor_NonPositiveCapacity_Throws(int boundedCapacity)
    {
        await Assert.That(() => new ConcurrentPriorityQueue<int, int>(boundedCapacity))
            .Throws<ArgumentOutOfRangeException>().Because(
                $"the public bounded constructor requires a strictly positive capacity; {boundedCapacity} is invalid");
    }

    /// <summary>
    /// On a full bounded queue, <see cref="ConcurrentPriorityQueue{TElement, TPriority}.Enqueue"/>
    /// throws <see cref="InvalidOperationException"/> (the throwing variant), while
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryEnqueue"/> returns
    /// <see langword="false"/> (the non-throwing variant) — and a rejected enqueue mutates nothing.
    /// </summary>
    [Test]
    public async Task Enqueue_BoundedFull_ThrowsWhileTryEnqueueReturnsFalse()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 8, boundedCapacity: 3, comparer: null);
        for (int i = 0; i < 3; i++)
        {
            queue.Enqueue(i, i);
        }

        await Assert.That(queue.Count).IsEqualTo(3).Because("the queue is filled to its bound");

        // The throwing variant throws on overflow.
        await Assert.That(() => queue.Enqueue(99, 99)).Throws<InvalidOperationException>().Because(
            "Enqueue on a full bounded queue throws InvalidOperationException");

        // The non-throwing variant returns false on overflow.
        await Assert.That(queue.TryEnqueue(98, 98)).IsFalse().Because(
            "TryEnqueue on a full bounded queue returns false rather than throwing");

        // A rejected enqueue mutated nothing: count and the gate are unchanged.
        await Assert.That(queue.Count).IsEqualTo(3).Because("a rejected enqueue does not add an element");
        await Assert.That(queue.DebugBoundedCountForTest).IsEqualTo(3).Because(
            "_boundedCount is conserved across a rejection — the failed reservation was exactly undone, none leaked");
    }

    /// <summary>
    /// On an unbounded queue, <see cref="ConcurrentPriorityQueue{TElement, TPriority}.Enqueue"/> never
    /// throws and <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryEnqueue"/> always returns
    /// <see langword="true"/>: the bounded gate is never touched (it stays permanently zero), driving
    /// the <c>_boundedCapacity &gt; 0</c> false arm.
    /// </summary>
    [Test]
    public async Task Enqueue_Unbounded_NeverRejectsAndNeverTouchesGate()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 8, boundedCapacity: -1, comparer: null);
        for (int i = 0; i < 1_000; i++)
        {
            await Assert.That(queue.TryEnqueue(i, i)).IsTrue().Because("an unbounded TryEnqueue always succeeds");
        }

        await Assert.That(queue.Count).IsEqualTo(1_000);
        await Assert.That(queue.DebugBoundedCountForTest).IsEqualTo(0).Because(
            "the unbounded path never increments the bounded gate, so it stays permanently zero");
    }

    /// <summary>
    /// The reservation rollback <c>catch</c> (#18): when a push throws <i>after</i> the bounded
    /// reservation was taken (driven here by a comparer that throws during the heap sift), the
    /// <c>catch</c> releases the reservation so the shared gate (<c>_boundedCount</c>) cannot be left
    /// permanently inflated by a transient failure.
    /// </summary>
    /// <remarks>
    /// The decisive, load-bearing assertion is that the gate returns to its pre-throw value: a leaked
    /// reservation would leave it one higher. (A throwing comparer corrupts the heap itself — the
    /// half-sifted node is left behind, the documented <see cref="PriorityQueue{TElement, TPriority}"/>
    /// behavior too — so this test only asserts the <i>reservation</i> accounting the #18 catch owns,
    /// not heap recoverability.)
    /// </remarks>
    [Test]
    public async Task Enqueue_PushThrows_RollsBackReservation()
    {
        // A comparer that throws on demand; the queue's bounded gate must recover the reservation when
        // the throw propagates out of TryLockedPush.
        var comparer = new ThrowOnDemandComparer();
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 1, boundedCapacity: 4, comparer: comparer);

        // Seed one element WITHOUT throwing (the first push onto an empty heap never invokes the
        // comparer — index 0 has no parent, so the sift-up loop does not run).
        queue.Enqueue(0, 0);
        await Assert.That(queue.DebugBoundedCountForTest).IsEqualTo(1).Because("one element reserved one slot");

        // Now arm the comparer to throw: the SECOND push sifts the new node against the root, invoking
        // the comparer, which throws. The reservation taken for this push must be rolled back.
        comparer.Throw = true;

        await Assert.That(() => queue.Enqueue(1, 1)).Throws<InvalidOperationException>().Because(
            "the throwing comparer propagates out of the heap push");

        // The decisive assertion: the gate returned to 1, not 2. A leaked reservation would leave it at
        // 2 — the #18 catch exactly undoes the reservation it took before the doomed push.
        await Assert.That(queue.DebugBoundedCountForTest).IsEqualTo(1).Because(
            "the reservation taken for the failed push was rolled back by the catch, not leaked");

        // A SECOND throwing push must not compound the gate either: each rollback is exact, so the gate
        // stays at 1 no matter how many doomed pushes are attempted (proves the catch is not one-shot).
        await Assert.That(() => queue.Enqueue(2, 2)).Throws<InvalidOperationException>();
        await Assert.That(queue.DebugBoundedCountForTest).IsEqualTo(1).Because(
            "a second doomed push rolls back its own reservation too — the gate is never left inflated");
    }

    /// <summary>
    /// Companion to the rollback test using an UNBOUNDED queue: when a push throws, no reservation was
    /// ever taken (the unbounded path skips the gate), so the throw simply propagates and the gate
    /// stays at its permanent zero — the <c>reserved == false</c> arm of the rollback <c>catch</c>.
    /// </summary>
    [Test]
    public async Task Enqueue_PushThrows_Unbounded_NoReservationToRollBack()
    {
        var comparer = new ThrowOnDemandComparer();
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 1, boundedCapacity: -1, comparer: comparer);

        queue.Enqueue(0, 0);
        comparer.Throw = true;

        await Assert.That(() => queue.Enqueue(1, 1)).Throws<InvalidOperationException>().Because(
            "the throwing comparer propagates out of the heap push on the unbounded path too");
        await Assert.That(queue.DebugBoundedCountForTest).IsEqualTo(0).Because(
            "an unbounded queue never reserved a slot, so the catch's reserved==false arm runs and the gate stays zero");
    }

    /// <summary>
    /// Bounded-capacity admission parity (DR-6): the admit/reject decision and the final
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.Count"/> are identical whether the
    /// ESA 2021 §4 buffers are ON (<c>bufferCapacity: 16</c>) or OFF (<c>bufferCapacity: 0</c>),
    /// at the same bound over an identical offered operation stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is the load-bearing parity.</b> The bound is enforced by the shared atomic
    /// reservation gate (<c>_boundedCount</c>), which is taken <i>before</i> any sub-queue is touched
    /// and is therefore structurally blind to whether an admitted element ultimately lands in the
    /// insertion buffer <c>I</c>, the deletion buffer <c>D</c>, or the arity-4 heap. The watermark
    /// admission view in turn reads the public <c>Count</c>, which sums each sub-queue's
    /// <c>VolatileCount</c> — and the core's T9 makes that per-stripe count <c>I + D + heap</c>, so a
    /// buffered resident is observed at the door exactly like a heaped one. If buffering ever
    /// over-admitted (a buffered element miscounted at the gate), the buffered queue would accept a push
    /// the unbuffered queue rejected, diverging this sequence — so a green run is a real proof, not a
    /// tautology.
    /// </para>
    /// <para>
    /// <b>Determinism.</b> Both queues pin <c>subQueueCount: 1</c> so the entire offered population
    /// funnels through one sub-queue's buffer (maximizing any I/D/heap routing divergence) and the
    /// single-threaded driver replays one fixed pseudo-random enqueue/dequeue stream — pushing both
    /// past and over the bound — against both queues. The per-operation admit results and the final
    /// count are then fully reproducible.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BoundedAdmission_Buffered_MatchesUnbufferedShedding()
    {
        const int bound = 8;
        const int operations = 400;
        const int prioritySpread = 32; // narrow enough that small/large keys both route through I and D

        // Two bounded single-sub-queue queues at the SAME bound: one buffered (ESA 2021 §4 cap 16),
        // one unbuffered (cap 0). subQueueCount: 1 forces the whole population through one stripe.
        var buffered = new ConcurrentPriorityQueue<int, int>(subQueueCount: 1, boundedCapacity: bound, comparer: null, bufferCapacity: 16);
        var unbuffered = new ConcurrentPriorityQueue<int, int>(subQueueCount: 1, boundedCapacity: bound, comparer: null, bufferCapacity: 0);

        // Replay ONE fixed operation stream against both queues, recording the per-enqueue admit
        // result (true = admitted, false = shed). Identical seed ⟹ identical stream ⟹ the recorded
        // admit sequences must coincide element-for-element if admission is buffer-agnostic.
        var rng = new Random(0xB0_17ED);
        var bufferedAdmits = new List<bool>(operations);
        var unbufferedAdmits = new List<bool>(operations);

        for (int op = 0; op < operations; op++)
        {
            // ~40% of operations are dequeues, the rest enqueues: this churns the buffered structure
            // through D-refills and I-flushes, so admission is exercised across every buffer state.
            bool isDequeue = rng.Next(100) < 40;
            if (isDequeue)
            {
                // A pop on either queue frees exactly one reservation; whether it drains D, the heap, or
                // is a no-op on an empty queue, both queues react identically because the bound only ever
                // tracks the logical element count, not the buffer layout.
                buffered.TryDequeue(out _, out _);
                unbuffered.TryDequeue(out _, out _);
            }
            else
            {
                int priority = rng.Next(prioritySpread);
                bufferedAdmits.Add(buffered.TryEnqueue(op, priority));
                unbufferedAdmits.Add(unbuffered.TryEnqueue(op, priority));
            }
        }

        // The decisive parity assertions: the admit/shed sequences coincide, and the queues end at the
        // same occupancy. A buffered over-admit would surface as a single diverging `true` here.
        await Assert.That(bufferedAdmits.SequenceEqual(unbufferedAdmits)).IsTrue().Because(
            "buffering on vs off must yield the same per-enqueue admit/shed decision at the same bound over an identical stream");
        await Assert.That(buffered.Count).IsEqualTo(unbuffered.Count).Because(
            "buffered and unbuffered bounded queues must reach the same final occupancy from the same operation stream");

        // And neither queue ever broke its bound (the watermark/reservation gate held for both regimes).
        await Assert.That(buffered.Count).IsLessThanOrEqualTo(bound).Because(
            "the buffered queue never admits beyond its bound");
        await Assert.That(unbuffered.Count).IsLessThanOrEqualTo(bound).Because(
            "the unbuffered queue never admits beyond its bound");
        await Assert.That(buffered.DebugBoundedCountForTest).IsEqualTo(unbuffered.DebugBoundedCountForTest).Because(
            "the reservation gate ends at the same value in both regimes — buffering takes no extra (or fewer) reservations");
    }

    /// <summary>
    /// A comparer that orders by the integer's natural order but throws an
    /// <see cref="InvalidOperationException"/> from <see cref="Compare"/> while <see cref="Throw"/> is
    /// set, used to drive the enqueue reservation-rollback <c>catch</c>.
    /// </summary>
    private sealed class ThrowOnDemandComparer : IComparer<int>
    {
        /// <summary>Gets or sets a value indicating whether <see cref="Compare"/> should throw.</summary>
        public bool Throw { get; set; }

        /// <inheritdoc/>
        public int Compare(int x, int y)
        {
            if (Throw)
            {
                throw new InvalidOperationException("Comparer deliberately throwing to drive the reservation rollback.");
            }

            return x.CompareTo(y);
        }
    }
}
