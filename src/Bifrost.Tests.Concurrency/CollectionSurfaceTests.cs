// =============================================================================
// <copyright file="CollectionSurfaceTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections;
using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Coverage for the unordered collection surface (DR-4):
/// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.ToArray"/>,
/// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.Clear"/>, value-type and interface
/// enumeration, and the empty-queue capacity-hint branch of <c>ToArray</c> (the
/// <c>hint &lt;= 0 ? 0</c> arm of the #18 <c>long</c>-accumulate clamp). The unreachable
/// <c>hint &gt;= int.MaxValue</c> arm of that clamp needs &gt; 2^31 elements and is dispositioned in
/// the verification task.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non-tautology.</b> The snapshot tests assert the exact multiset of <c>(element, priority)</c>
/// pairs present, so a <c>ToArray</c> that dropped a stripe, double-counted, or returned the wrong
/// element would fail. The clear tests assert the queue is genuinely emptied (and that a bounded
/// queue's freed capacity is reusable), not merely that <c>Clear</c> returned.
/// </para>
/// </remarks>
public class CollectionSurfaceTests
{
    /// <summary>
    /// Builds a queue with an exact, pinned sub-queue count (named arguments are mandatory on the
    /// internal core constructor) and the default <c>int</c> comparer.
    /// </summary>
    /// <param name="subQueueCount">The exact sub-queue count to pin.</param>
    /// <param name="boundedCapacity">The bounded capacity, or <c>-1</c> for unbounded.</param>
    /// <returns>A pinned-size <c>(int, int)</c> queue.</returns>
    private static ConcurrentPriorityQueue<int, int> NewPinned(int subQueueCount, int boundedCapacity = -1)
        => new(subQueueCount: subQueueCount, boundedCapacity: boundedCapacity, comparer: null);

    /// <summary>
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.ToArray"/> on a populated queue returns
    /// every element exactly once — the snapshot contains the full multiset of inserted pairs,
    /// regardless of which stripe each landed in, and the array length equals the element count.
    /// </summary>
    [Test]
    public async Task ToArray_PopulatedQueue_ContainsEveryElementOnce()
    {
        var queue = NewPinned(subQueueCount: 8);
        const int n = 500;
        for (int i = 0; i < n; i++)
        {
            queue.Enqueue(i, i);
        }

        (int Element, int Priority)[] snapshot = queue.ToArray();

        await Assert.That(snapshot.Length).IsEqualTo(n).Because("the snapshot has one entry per element");

        // Exact multiset check: every priority 0..n-1 appears exactly once, element == priority.
        int[] sortedPriorities = snapshot.Select(t => t.Priority).OrderBy(p => p).ToArray();
        for (int i = 0; i < n; i++)
        {
            await Assert.That(sortedPriorities[i]).IsEqualTo(i).Because(
                $"the snapshot must contain priority {i} exactly once (no drops, no duplicates)");
        }

        bool allElementsMatch = snapshot.All(t => t.Element == t.Priority);
        await Assert.That(allElementsMatch).IsTrue().Because("each entry carries its own element == priority pair");
    }

    /// <summary>
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.ToArray"/> on an empty queue returns an
    /// empty array — driving the <c>hint &lt;= 0 ? 0</c> arm of the capacity-hint clamp (every stripe's
    /// <c>VolatileCount</c> is zero, so the accumulated hint is zero).
    /// </summary>
    [Test]
    public async Task ToArray_EmptyQueue_ReturnsEmptyArray()
    {
        var queue = NewPinned(subQueueCount: 4);

        (int Element, int Priority)[] snapshot = queue.ToArray();

        await Assert.That(snapshot).IsNotNull().Because("ToArray always returns a real array, never null");
        await Assert.That(snapshot.Length).IsEqualTo(0).Because("an empty queue snapshots to a zero-length array");
    }

    /// <summary>
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.ToArray"/> is unordered: it makes no
    /// priority-ordering claim. The test asserts the snapshot's <i>multiset</i> is correct while not
    /// requiring sorted order (sorting it recovers the population), matching the documented
    /// UnorderedItems precedent.
    /// </summary>
    [Test]
    public async Task ToArray_IsUnordered_ButMultisetIsExact()
    {
        var queue = NewPinned(subQueueCount: 8);
        int[] population = [9, 1, 7, 3, 5, 2, 8, 4, 6, 0];
        foreach (int p in population)
        {
            queue.Enqueue(p, p);
        }

        (int Element, int Priority)[] snapshot = queue.ToArray();
        int[] recovered = snapshot.Select(t => t.Priority).OrderBy(p => p).ToArray();

        await Assert.That(recovered.Length).IsEqualTo(population.Length);
        for (int i = 0; i < population.Length; i++)
        {
            await Assert.That(recovered[i]).IsEqualTo(i).Because(
                "sorting the unordered snapshot recovers the exact inserted population 0..9");
        }
    }

    /// <summary>
    /// The value-type <see cref="ConcurrentPriorityQueue{TElement, TPriority}.GetEnumerator"/> path
    /// (bound by a direct <c>foreach</c>) iterates the full snapshot exactly once, and
    /// <c>Reset</c> rewinds it so a second pass yields the same multiset.
    /// </summary>
    [Test]
    public async Task Enumerator_ValueType_IteratesSnapshotAndResets()
    {
        var queue = NewPinned(subQueueCount: 8);
        const int n = 100;
        for (int i = 0; i < n; i++)
        {
            queue.Enqueue(i, i);
        }

        // First pass via the value-type enumerator (no boxing).
        var firstPass = new List<int>();
        var enumerator = queue.GetEnumerator();
        while (enumerator.MoveNext())
        {
            firstPass.Add(enumerator.Current.Priority);
        }

        await Assert.That(firstPass.Count).IsEqualTo(n).Because("the enumerator yields every snapshot entry");

        // Reset and re-iterate: the same multiset comes back, proving Reset rewinds to before-first.
        enumerator.Reset();
        var secondPass = new List<int>();
        while (enumerator.MoveNext())
        {
            secondPass.Add(enumerator.Current.Priority);
        }

        await Assert.That(secondPass.Count).IsEqualTo(n).Because("Reset rewinds, so the second pass yields the full snapshot again");
        await Assert.That(firstPass.OrderBy(x => x).SequenceEqual(secondPass.OrderBy(x => x))).IsTrue().Because(
            "both passes iterate the identical captured snapshot");

        enumerator.Dispose(); // no-op, but exercises the IDisposable surface.
    }

    /// <summary>
    /// The boxing interface enumeration paths
    /// (<see cref="IEnumerable{T}.GetEnumerator"/> and the non-generic <see cref="IEnumerable.GetEnumerator"/>)
    /// both yield the same full snapshot, and the non-generic <c>Current</c> boxes each entry correctly.
    /// </summary>
    [Test]
    public async Task Enumerator_InterfacePaths_IterateFullSnapshot()
    {
        var queue = NewPinned(subQueueCount: 4);
        int[] population = [3, 1, 2, 0];
        foreach (int p in population)
        {
            queue.Enqueue(p, p);
        }

        // Generic IEnumerable<T> path (boxed enumerator).
        IEnumerable<(int Element, int Priority)> generic = queue;
        var genericPriorities = generic.Select(t => t.Priority).OrderBy(x => x).ToArray();
        await Assert.That(genericPriorities.SequenceEqual([0, 1, 2, 3])).IsTrue().Because(
            "the generic IEnumerable<T> path yields the full snapshot");

        // Non-generic IEnumerable path: drive IEnumerator.Current (the boxing object accessor).
        IEnumerable nonGeneric = queue;
        var boxed = new List<int>();
        IEnumerator e = nonGeneric.GetEnumerator();
        while (e.MoveNext())
        {
            var entry = ((int Element, int Priority))e.Current!;
            boxed.Add(entry.Priority);
        }

        await Assert.That(boxed.OrderBy(x => x).SequenceEqual([0, 1, 2, 3])).IsTrue().Because(
            "the non-generic IEnumerable path yields the full snapshot through the boxed Current");
    }

    /// <summary>
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.Clear"/> on an unbounded queue empties
    /// it: the queue reports empty, <c>ToArray</c> is empty, and the queue stays usable for subsequent
    /// enqueue/dequeue.
    /// </summary>
    [Test]
    public async Task Clear_UnboundedQueue_EmptiesAndStaysUsable()
    {
        var queue = NewPinned(subQueueCount: 8);
        const int n = 300;
        for (int i = 0; i < n; i++)
        {
            queue.Enqueue(i, i);
        }

        await Assert.That(queue.Count).IsEqualTo(n).Because("the queue is populated before the clear");

        queue.Clear();

        await Assert.That(queue.IsEmpty).IsTrue().Because("Clear empties the quiescent queue");
        await Assert.That(queue.Count).IsEqualTo(0).Because("Count is zero after a quiescent Clear");
        await Assert.That(queue.ToArray().Length).IsEqualTo(0).Because("ToArray reflects the emptied queue");
        await Assert.That(queue.TryDequeue(out _, out _)).IsFalse().Because("nothing remains to dequeue after Clear");

        // Still functional: a fresh enqueue is retrievable.
        queue.Enqueue(42, 42);
        await Assert.That(queue.TryDequeue(out int element, out int priority)).IsTrue();
        await Assert.That(element).IsEqualTo(42).Because("the queue is fully functional after Clear");
        await Assert.That(priority).IsEqualTo(42);
    }

    /// <summary>
    /// On a BOUNDED queue, <see cref="ConcurrentPriorityQueue{TElement, TPriority}.Clear"/> releases
    /// every cleared element's reservation: a queue filled to its bound (so the next enqueue throws)
    /// becomes fully re-enqueueable to the bound again after a clear — proving the bounded-reservation
    /// release branch (<c>_boundedCapacity &gt; 0 &amp;&amp; removed &gt; 0</c>) ran.
    /// </summary>
    [Test]
    public async Task Clear_BoundedQueue_ReleasesReservationsForReuse()
    {
        var queue = NewPinned(subQueueCount: 8, boundedCapacity: 50);
        for (int i = 0; i < 50; i++)
        {
            queue.Enqueue(i, i);
        }

        await Assert.That(queue.DebugBoundedCountForTest).IsEqualTo(50).Because("the bounded gate is fully reserved");

        // The bound is reached: the next enqueue throws.
        await Assert.That(() => queue.Enqueue(999, 999)).Throws<InvalidOperationException>().Because(
            "a full bounded queue rejects further enqueues before the clear");

        queue.Clear();

        await Assert.That(queue.IsEmpty).IsTrue().Because("Clear empties the bounded queue");
        await Assert.That(queue.DebugBoundedCountForTest).IsEqualTo(0).Because(
            "Clear released every cleared element's bounded reservation");

        // Capacity is reusable up to the bound again — the reservation release was real, not cosmetic.
        for (int i = 0; i < 50; i++)
        {
            queue.Enqueue(i, i);
        }

        await Assert.That(queue.Count).IsEqualTo(50).Because("the full bound is re-enqueueable after Clear freed the reservations");
        await Assert.That(() => queue.Enqueue(1000, 1000)).Throws<InvalidOperationException>().Because(
            "the bound is enforced again at the same capacity after the clear-and-refill");
    }

    /// <summary>
    /// <see cref="ConcurrentPriorityQueue{TElement, TPriority}.Clear"/> on an already-empty queue is a
    /// no-op that does not throw or fabricate state — driving the <c>removed &lt;= 0</c> early-return
    /// path inside every sub-queue's <c>LockedClear</c>.
    /// </summary>
    [Test]
    public async Task Clear_AlreadyEmptyQueue_IsHarmlessNoOp()
    {
        var queue = NewPinned(subQueueCount: 4);

        queue.Clear(); // every stripe takes the removed <= 0 early return.

        await Assert.That(queue.IsEmpty).IsTrue().Because("clearing an empty queue leaves it empty");
        await Assert.That(queue.Count).IsEqualTo(0);
        await Assert.That(queue.ToArray().Length).IsEqualTo(0);
    }
}
