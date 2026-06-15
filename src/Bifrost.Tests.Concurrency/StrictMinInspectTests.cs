// =============================================================================
// <copyright file="StrictMinInspectTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Coverage for the STRICT-min inspection surface (DR-4):
/// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryDequeueMin"/>,
/// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryPeek"/>, the private
/// <c>TryScanForMinimum</c>/<c>TryScanRunnerUp</c> lock-free scans they reuse, and the
/// <c>SubQueue.PopHeldRoot</c> pop-under-held-lock path the strict-min dequeue relies on (#18).
/// </summary>
/// <remarks>
/// <para>
/// Unlike the relaxed two-choice <c>TryDequeue</c>, the strict path scans <i>every</i> sub-queue's
/// published top and pops the true global minimum exactly. These tests pin the sub-queue count
/// through the internal constructor so the scans are deterministic, and drive the backing
/// sub-queues directly (via <c>SubQueuesForTest</c>) where a branch needs a specific cross-stripe
/// arrangement — the same direct-drive style the occupancy and rank-error tests use.
/// </para>
/// <para>
/// <b>Non-tautology.</b> Every assertion below names a concrete expected element/priority and would
/// fail if the strict path returned the wrong stripe's element, dropped an element, or mis-ordered
/// the winner against the runner-up — none of them merely re-assert a method's own return value.
/// </para>
/// </remarks>
public class StrictMinInspectTests
{
    /// <summary>
    /// Builds a queue with an exact, pinned sub-queue count (named arguments are mandatory on the
    /// internal core constructor; see its remarks) and the default <c>int</c> comparer.
    /// </summary>
    /// <param name="subQueueCount">The exact sub-queue count to pin.</param>
    /// <returns>A pinned-size unbounded <c>(int, int)</c> queue.</returns>
    private static ConcurrentPriorityQueue<int, int> NewPinned(int subQueueCount)
        => new(subQueueCount: subQueueCount, boundedCapacity: -1, comparer: null);

    /// <summary>
    /// Builds a pinned-size unbounded queue with ESA 2021 §4 buffering ENABLED at the given logical
    /// capacity, so the strict-min revalidation path must read <c>D.front()</c> (the buffered top),
    /// not the arity-4 heap root. Single sub-queue by default so the resident minima land in the
    /// deletion buffer <c>D</c> deterministically.
    /// </summary>
    /// <param name="subQueueCount">The exact sub-queue count to pin.</param>
    /// <param name="bufferCapacity">The logical buffer capacity <c>C</c> (1..16).</param>
    /// <returns>A pinned-size unbounded buffered <c>(int, int)</c> queue.</returns>
    private static ConcurrentPriorityQueue<int, int> NewBufferedPinned(int subQueueCount, int bufferCapacity)
        => new(subQueueCount: subQueueCount, boundedCapacity: -1, comparer: null, bufferCapacity: bufferCapacity);

    /// <summary>
    /// Single-threaded, the strict <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryDequeueMin"/>
    /// drains the population in <i>exact</i> ascending priority order — it always returns the TRUE
    /// global minimum, unlike the relaxed two-choice path. A scattered insertion order across many
    /// stripes makes the full-scan behavior load-bearing: a relaxed pop would routinely return a
    /// non-minimum and break the strictly-increasing sequence.
    /// </summary>
    [Test]
    public async Task TryDequeueMin_SingleThreadDrain_ReturnsExactGlobalMinimumOrder()
    {
        var queue = NewPinned(subQueueCount: 16);
        const int n = 2_000;

        // Insert 0..n-1 in a shuffled order so the elements scatter across stripes; the strict drain
        // must still surface them in perfect ascending order regardless of where they landed.
        var rng = new Random(0xBEEF);
        int[] order = Enumerable.Range(0, n).ToArray();
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        foreach (int p in order)
        {
            queue.Enqueue(p, p);
        }

        int previous = -1;
        for (int i = 0; i < n; i++)
        {
            bool ok = queue.TryDequeueMin(out int element, out int priority);
            await Assert.That(ok).IsTrue().Because($"the strict drain must yield element {i} of {n}");
            await Assert.That(priority).IsEqualTo(i).Because(
                $"TryDequeueMin must return the exact global minimum in order; expected {i}, got {priority}");
            await Assert.That(element).IsEqualTo(priority).Because("element == priority by construction");
            await Assert.That(priority).IsGreaterThan(previous).Because("the strict drain is monotonically increasing");
            previous = priority;
        }

        // The drain is exact: nothing left behind, nothing fabricated.
        await Assert.That(queue.TryDequeueMin(out _, out _)).IsFalse().Because("the queue is fully drained");
        await Assert.That(queue.IsEmpty).IsTrue().Because("a strict drain removes every element exactly once");
    }

    /// <summary>
    /// REGRESSION (#18): with ESA 2021 §4 buffering ENABLED, the strict
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryDequeueMin"/> still drains the full
    /// population in exact ascending order. The resident minima live in the deletion buffer <c>D</c>
    /// (whose <c>D.front()</c> is the published top), while the arity-4 heap holds the larger keys and
    /// can even be empty while <c>D</c> is full. The under-lock revalidation must peek <c>D.front()</c>,
    /// NOT the heap root: with the bug present it peeked the heap root — which is strictly larger than
    /// <c>D.front()</c> (or reports empty when the heap is empty) — so revalidation kept failing and the
    /// method gave up early, returning <see langword="false"/> with elements still resident
    /// (the AOT-smoke symptom: <c>TryDequeueMin</c> drained 0 of 4096).
    /// </summary>
    [Test]
    public async Task TryDequeueMin_Buffered_DrainsAllInStrictOrder()
    {
        // Single sub-queue with buffering on: every element funnels through D + heap on ONE stripe, so
        // the strict path's under-lock revalidation read is unambiguously the buffered front.
        var queue = NewBufferedPinned(subQueueCount: 1, bufferCapacity: 16);

        // > bufferCapacity elements so D fills, the heap is non-empty, and pops force repeated
        // refills of D from the heap — exactly the regime where the heap root != D.front().
        const int n = 200;

        // Shuffled insertion so a wrong revalidation cannot accidentally pass by lucky ordering.
        var rng = new Random(0xC0FFEE);
        int[] order = Enumerable.Range(0, n).ToArray();
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        foreach (int p in order)
        {
            queue.Enqueue(p, p);
        }

        await Assert.That(queue.Count).IsEqualTo(n).Because("all n elements are resident before the drain");

        int previous = -1;
        for (int i = 0; i < n; i++)
        {
            bool ok = queue.TryDequeueMin(out int element, out int priority);
            await Assert.That(ok).IsTrue().Because(
                $"the buffered strict drain must yield element {i} of {n}; a false here is the #18 heap-root-revalidation bug");
            await Assert.That(priority).IsEqualTo(i).Because(
                $"TryDequeueMin must serve D.front() in exact ascending order; expected {i}, got {priority}");
            await Assert.That(element).IsEqualTo(priority).Because("element == priority by construction");
            await Assert.That(priority).IsGreaterThan(previous).Because("the buffered strict drain is monotonically increasing");
            previous = priority;
        }

        await Assert.That(queue.TryDequeueMin(out _, out _)).IsFalse().Because("the buffered queue is fully drained");
        await Assert.That(queue.IsEmpty).IsTrue().Because("a buffered strict drain removes every element exactly once");
    }

    /// <summary>
    /// REGRESSION (#18): with buffering ENABLED and the global minimum resident in the deletion buffer
    /// <c>D</c> while the heap holds only larger keys,
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryPeek"/> returns that buffered minimum
    /// (non-destructively). With the bug present the under-lock revalidation peeked the heap root —
    /// strictly larger than <c>D.front()</c> — so the no-worse-than-scanned-minimum check failed every
    /// attempt and TryPeek returned <see langword="false"/> on a non-empty queue.
    /// </summary>
    [Test]
    public async Task TryPeek_Buffered_ReturnsResidentMinimum()
    {
        var queue = NewBufferedPinned(subQueueCount: 1, bufferCapacity: 16);

        // Enqueue more than C so D holds the smallest C entries (front = the global minimum) while the
        // heap retains the larger keys — the arrangement where heap root != D.front().
        const int n = 50;
        for (int p = n - 1; p >= 0; p--)
        {
            queue.Enqueue(p, p);
        }

        // The published top is D.front() == 0, but 0 does NOT sit at the heap root (it was buffered).
        await Assert.That(queue.TryPeek(out int e1, out int p1)).IsTrue().Because(
            "TryPeek must see the buffered resident minimum 0 in D.front(), not fail against the heap root");
        await Assert.That(p1).IsEqualTo(0).Because("the resident minimum lives in the deletion buffer D");
        await Assert.That(e1).IsEqualTo(0);

        // Non-destructive: a second peek still sees 0, and the count is unchanged.
        await Assert.That(queue.TryPeek(out _, out int p2)).IsTrue();
        await Assert.That(p2).IsEqualTo(0).Because("a second peek still sees the buffered minimum — peek does not remove");
        await Assert.That(queue.Count).IsEqualTo(n).Because("the two buffered peeks removed nothing");

        // And the next strict pop removes exactly that peeked minimum.
        await Assert.That(queue.TryDequeueMin(out _, out int popped)).IsTrue();
        await Assert.That(popped).IsEqualTo(0).Because("the peeked buffered minimum is the next element popped");
    }

    /// <summary>
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryDequeueMin"/> returns the global
    /// minimum even when it lives in a different stripe than the most-recently-touched one, exercising
    /// the cross-stripe <c>TryScanForMinimum</c> winner selection and the <c>TryScanRunnerUp</c>
    /// revalidation against a smaller candidate in another stripe.
    /// </summary>
    [Test]
    public async Task TryDequeueMin_MinimumInDistinctStripe_PopsAcrossStripes()
    {
        var queue = NewPinned(subQueueCount: 8);
        SubQueue<int, int>[] stripes = queue.SubQueuesForTest;

        // Stripe 0 holds {50, 60}; stripe 3 holds the true minimum {5} plus {70}; stripe 5 holds {9}.
        // Direct-drive each stripe so the placement is deterministic (the public Enqueue scatters
        // randomly and could not pin which stripe holds the minimum).
        PushDirect(stripes[0], 50);
        PushDirect(stripes[0], 60);
        PushDirect(stripes[3], 70);
        PushDirect(stripes[3], 5);
        PushDirect(stripes[5], 9);

        // First strict pop: the global minimum is 5 in stripe 3 — not stripe 0's 50 or stripe 5's 9.
        await Assert.That(queue.TryDequeueMin(out int e1, out int p1)).IsTrue();
        await Assert.That(p1).IsEqualTo(5).Because("the strict min is 5 in stripe 3, found by the cross-stripe scan");
        await Assert.That(e1).IsEqualTo(5);

        // Second strict pop: now 9 (stripe 5) is the runner-up-turned-winner.
        await Assert.That(queue.TryDequeueMin(out int _, out int p2)).IsTrue();
        await Assert.That(p2).IsEqualTo(9).Because("after 5 is removed, 9 in stripe 5 is the new global minimum");

        // Remaining ascend exactly: 50, 60, 70.
        await Assert.That(PopMinPriority(queue)).IsEqualTo(50);
        await Assert.That(PopMinPriority(queue)).IsEqualTo(60);
        await Assert.That(PopMinPriority(queue)).IsEqualTo(70);
        await Assert.That(queue.TryDequeueMin(out _, out _)).IsFalse().Because("all five elements were drained in exact order");
    }

    /// <summary>
    /// On an empty queue both strict-inspection entry points report the correct empty semantics:
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryDequeueMin"/> and
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryPeek"/> each return
    /// <see langword="false"/> with defaulted out parameters, driving the
    /// <c>TryScanForMinimum</c> "found nothing" branch.
    /// </summary>
    [Test]
    public async Task StrictInspect_EmptyQueue_ReturnsFalseWithDefaults()
    {
        var queue = NewPinned(subQueueCount: 4);

        await Assert.That(queue.TryDequeueMin(out int dElement, out int dPriority)).IsFalse().Because(
            "TryDequeueMin on an empty queue returns false");
        await Assert.That(dElement).IsEqualTo(0).Because("the out element defaults on the empty path");
        await Assert.That(dPriority).IsEqualTo(0).Because("the out priority defaults on the empty path");

        await Assert.That(queue.TryPeek(out int pElement, out int pPriority)).IsFalse().Because(
            "TryPeek on an empty queue returns false");
        await Assert.That(pElement).IsEqualTo(0).Because("the out element defaults on the empty peek path");
        await Assert.That(pPriority).IsEqualTo(0).Because("the out priority defaults on the empty peek path");
    }

    /// <summary>
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryPeek"/> returns the global minimum
    /// without removing it: the very next strict pop yields that same minimum, proving the peek was
    /// non-destructive while exercising the lock-free scan + single-lock retrieval path.
    /// </summary>
    [Test]
    public async Task TryPeek_ReturnsGlobalMinimum_WithoutRemoving()
    {
        var queue = NewPinned(subQueueCount: 8);
        SubQueue<int, int>[] stripes = queue.SubQueuesForTest;

        PushDirect(stripes[1], 40);
        PushDirect(stripes[4], 3);   // the global minimum
        PushDirect(stripes[6], 20);

        // Peek twice: idempotent, and both see the minimum 3 in stripe 4.
        await Assert.That(queue.TryPeek(out int e1, out int p1)).IsTrue();
        await Assert.That(p1).IsEqualTo(3).Because("TryPeek returns the global minimum 3");
        await Assert.That(e1).IsEqualTo(3);

        await Assert.That(queue.TryPeek(out int _, out int p2)).IsTrue();
        await Assert.That(p2).IsEqualTo(3).Because("a second peek still sees 3 — peek does not remove");

        // The element is still present: the strict pop now removes exactly that 3.
        await Assert.That(queue.Count).IsEqualTo(3).Because("nothing was removed by the two peeks");
        await Assert.That(queue.TryDequeueMin(out _, out int popped)).IsTrue();
        await Assert.That(popped).IsEqualTo(3).Because("the peeked minimum is the next element popped");
    }

    /// <summary>
    /// The <c>PopHeldRoot</c> pop-under-held-lock path (#18) is the single entry point
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryDequeueMin"/> uses after it
    /// revalidates the root under the winner's lock. Exercised directly on a sub-queue: with the lock
    /// already held, <c>PopHeldRoot</c> reports <see cref="SubQueuePopStatus.Success"/> and removes the
    /// root, then <see cref="SubQueuePopStatus.Empty"/> once drained — and it republishes the new top
    /// so the queue-level strict path stays correct.
    /// </summary>
    [Test]
    public async Task PopHeldRoot_UnderHeldLock_PopsRootThenReportsEmpty()
    {
        var subQueue = new SubQueue<int, int>(comparer: null, index: 0, occupancy: new ulong[1]);
        PushDirect(subQueue, 30);
        PushDirect(subQueue, 10); // becomes the root (min-heap)
        PushDirect(subQueue, 20);

        // The caller owns the lock for PopHeldRoot — that is the whole point of the #18 path: it pops
        // the exact root it revalidated, with no intervening unlock window.
        SubQueuePopStatus s1, s2, s3, s4;
        int e1, p1, e2, p2, e3, p3, e4, p4;
        lock (subQueue.SyncLock)
        {
            s1 = subQueue.PopHeldRoot(out e1, out p1);
            s2 = subQueue.PopHeldRoot(out e2, out p2);
            s3 = subQueue.PopHeldRoot(out e3, out p3);
            s4 = subQueue.PopHeldRoot(out e4, out p4);
        }

        await Assert.That(s1).IsEqualTo(SubQueuePopStatus.Success).Because("the first held-lock pop succeeds");
        await Assert.That(p1).IsEqualTo(10).Because("PopHeldRoot removes the min-heap root first");
        await Assert.That(e1).IsEqualTo(10);

        await Assert.That(s2).IsEqualTo(SubQueuePopStatus.Success);
        await Assert.That(p2).IsEqualTo(20).Because("the heap reorders to expose 20 after 10 is popped");

        await Assert.That(s3).IsEqualTo(SubQueuePopStatus.Success);
        await Assert.That(p3).IsEqualTo(30);

        // The drain crossed the non-empty->empty boundary: PopHeldRoot republished empty.
        await Assert.That(s4).IsEqualTo(SubQueuePopStatus.Empty).Because(
            "a held-lock pop on a drained heap reports Empty, never Contended (the caller holds the lock)");
        await Assert.That(subQueue.HeapSize).IsEqualTo(0).Because("the heap is fully drained");
        await Assert.That(subQueue.VolatileCount).IsEqualTo(0).Because("PopHeldRoot writes the striped count to zero on drain");
    }

    /// <summary>
    /// Concurrent stress: with the strict <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryDequeueMin"/>
    /// draining a fully-enqueued population across many consumers (the quiescent-drain technique),
    /// conservation holds exactly — every element is popped exactly once and none is lost or
    /// duplicated — exercising the under-lock revalidation/rescan loop under real contention.
    /// </summary>
    [Test]
    public async Task TryDequeueMin_ConcurrentDrain_ConservesEveryElement()
    {
        var queue = NewPinned(subQueueCount: 16);
        const int n = 20_000;
        const int consumerCount = 6;

        // PHASE 1: enqueue everything single-threaded so a false-while-non-empty has no honest excuse.
        for (int i = 0; i < n; i++)
        {
            queue.Enqueue(i, i);
        }

        long remaining = n;
        int[] popCounts = new int[n];

        // PHASE 2: consumers drain via the STRICT path concurrently. Each popped priority is unique
        // 0..n-1, so a per-priority tally proves no element is ever popped twice.
        var consumers = new Thread[consumerCount];
        for (int c = 0; c < consumerCount; c++)
        {
            consumers[c] = new Thread(() =>
            {
                while (Volatile.Read(ref remaining) > 0)
                {
                    if (queue.TryDequeueMin(out _, out int priority))
                    {
                        Interlocked.Increment(ref popCounts[priority]);
                        Interlocked.Decrement(ref remaining);
                    }
                }
            })
            { IsBackground = true, Name = $"strict-drain-consumer-{c}" };
        }

        foreach (var t in consumers)
        {
            t.Start();
        }

        foreach (var t in consumers)
        {
            bool joined = t.Join(TimeSpan.FromSeconds(15));
            await Assert.That(joined).IsTrue().Because(
                "consumer threads must complete; a timeout signals a drain-progress regression (lost pop) rather than hanging CI");
        }

        // Conservation: every priority popped exactly once, none lost, none duplicated.
        int poppedOnce = popCounts.Count(x => x == 1);
        int duplicated = popCounts.Count(x => x > 1);
        int missing = popCounts.Count(x => x == 0);

        await Assert.That(duplicated).IsEqualTo(0).Because("no element may be popped more than once under the strict path");
        await Assert.That(missing).IsEqualTo(0).Because("every element must be popped under a complete strict drain");
        await Assert.That(poppedOnce).IsEqualTo(n).Because("conservation: exactly n distinct elements popped once each");
        await Assert.That(queue.IsEmpty).IsTrue().Because("the queue is empty after a full strict drain");
    }

    /// <summary>
    /// Pushes a single entry into a specific sub-queue under its lock, mirroring the production
    /// push+publish composition (heap push then locked-pop bookkeeping). Used to pin exactly which
    /// stripe holds which element, which the randomized public <c>Enqueue</c> cannot do.
    /// </summary>
    /// <param name="subQueue">The target sub-queue.</param>
    /// <param name="priority">The element-and-priority value (element == priority).</param>
    private static void PushDirect(SubQueue<int, int> subQueue, int priority)
        => subQueue.TryLockedPush(priority, priority);

    /// <summary>Pops the strict global minimum priority, asserting success first.</summary>
    /// <param name="queue">The queue to pop from.</param>
    /// <returns>The popped minimum priority.</returns>
    private static int PopMinPriority(ConcurrentPriorityQueue<int, int> queue)
    {
        bool ok = queue.TryDequeueMin(out _, out int priority);
        return ok ? priority : int.MinValue;
    }
}
