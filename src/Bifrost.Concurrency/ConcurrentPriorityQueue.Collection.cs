// =============================================================================
// <copyright file="ConcurrentPriorityQueue.Collection.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections;

namespace Bifrost.Concurrency;

/// <content>
/// The unordered collection surface: <see cref="ToArray"/> copies every element-and-
/// priority pair into a new array, <see cref="GetEnumerator"/> iterates that snapshot through a
/// public value-type <see cref="Enumerator"/>, and <see cref="Clear"/> empties the queue one
/// sub-queue at a time. The type satisfies
/// <see cref="IEnumerable{T}"/> and <see cref="IReadOnlyCollection{T}"/> over
/// <c>(TElement Element, TPriority Priority)</c>; the collection's <see cref="IReadOnlyCollection{T}.Count"/>
/// binds to the existing snapshot-semantics <see cref="Count"/>, so no new count
/// member is introduced here.
/// </content>
/// <remarks>
/// <para>
/// Weakly consistent and unordered: <see cref="ToArray"/> walks the lock-striped
/// sub-queues <i>one at a time</i>, taking each sub-queue's lock only long enough to copy that
/// sub-queue's heap segment and releasing it before moving to the next, with no global freeze
/// and no claim of a single moment-in-time consistent view across sub-queues. A sub-queue copied
/// early and one copied late may reflect different instants of concurrent mutation, the weakly
/// consistent enumeration guarantee of
/// <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/> and
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>. The result is
/// unordered: like <see cref="PriorityQueue{TElement, TPriority}.UnorderedItems"/>, it makes
/// no priority-ordering guarantee; heap segments are concatenated in sub-queue order and each
/// segment is in arbitrary heap-array order.
/// </para>
/// </remarks>
public sealed partial class ConcurrentPriorityQueue<TElement, TPriority>
    : IEnumerable<(TElement Element, TPriority Priority)>,
      IReadOnlyCollection<(TElement Element, TPriority Priority)>
{
    /// <summary>
    /// Copies the queue's elements and their priorities to a new, unordered array.
    /// </summary>
    /// <returns>
    /// A new array containing one <c>(element, priority)</c> entry per element in the queue at the
    /// time of the call. The array is unordered; no priority ordering is implied.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Weakly consistent snapshot: the sub-queues are copied one at a time, each
    /// under its own lock held only for the duration of that sub-queue's copy and released before
    /// the next. There is no global freeze and no cross-sub-queue consistency claim: the array is a
    /// momentary, weakly consistent view of a queue that other threads may be mutating concurrently
    /// (the <see cref="System.Collections.Concurrent.ConcurrentQueue{T}.ToArray"/> and
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/> precedent).
    /// An element enqueued or dequeued during the walk may or may not appear, depending on which
    /// sub-queue it lands in and when that sub-queue is copied.
    /// </para>
    /// <para>
    /// Unordered result: the returned entries carry no priority ordering, matching the
    /// <see cref="PriorityQueue{TElement, TPriority}.UnorderedItems"/> precedent. Callers that need
    /// ordered output must sort the result themselves.
    /// </para>
    /// <para>
    /// <b>Thread Safety:</b> This method is thread-safe and may be called concurrently with
    /// enqueues, dequeues, peeks, and other snapshots. It briefly blocks on each sub-queue's lock in
    /// turn while copying that sub-queue; it never holds more than one sub-queue lock at a time.
    /// </para>
    /// </remarks>
    public (TElement Element, TPriority Priority)[] ToArray()
    {
        // A right-sized starting capacity from the unlocked striped counts avoids most regrowth;
        // the list is the authoritative size because concurrent mutation may change a sub-queue's
        // count between the capacity hint and its own locked copy.
        int hint = _queues.Sum(t => t.VolatileCount);

        var buffer = new List<(TElement Element, TPriority Priority)>(hint < 0 ? 0 : hint);

        // One sub-queue at a time: each SnapshotTo takes that sub-queue's lock, copies its heap
        // segment, and releases before the next; no global freeze.
        foreach (var t in _queues)
        {
            t.SnapshotTo(buffer);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Removes all elements from the queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Weakly consistent: like <see cref="ToArray"/>, the sub-queues are cleared one
    /// at a time, each under its own lock held only for that sub-queue's clear, with no global
    /// freeze. Elements enqueued concurrently into a sub-queue that was already cleared survive the
    /// call, so on a queue under concurrent mutation <see cref="Clear"/> guarantees only that every
    /// element present in a sub-queue at the moment that sub-queue was cleared is removed. At
    /// quiescence the queue is exactly empty. (<see cref="System.Collections.Concurrent.ConcurrentQueue{T}.Clear"/>
    /// and <see cref="PriorityQueue{TElement, TPriority}.Clear"/> are the API precedent.)
    /// </para>
    /// <para>
    /// On a bounded queue, every removed element releases its bounded-capacity reservation, so
    /// capacity freed by <see cref="Clear"/> is immediately available to concurrent enqueues.
    /// </para>
    /// <para>
    /// <b>Thread Safety:</b> This method is thread-safe and may be called concurrently with
    /// enqueues, dequeues, peeks, and snapshots. It briefly blocks on each sub-queue's lock in turn;
    /// it never holds more than one sub-queue lock at a time.
    /// </para>
    /// </remarks>
    public void Clear()
    {
        foreach (var t in _queues)
        {
            int removed = t.LockedClear();

            // Release the bounded reservations for everything this stripe held (no-op unbounded).
            if (_boundedCapacity > 0 && removed > 0)
            {
                Interlocked.Add(ref _boundedCount, -removed);
            }
        }
    }

    /// <summary>
    /// Returns a value-type enumerator that iterates an unordered, weakly consistent snapshot of the
    /// queue.
    /// </summary>
    /// <returns>
    /// An <see cref="Enumerator"/> over a <see cref="ToArray"/> snapshot. Because the return type is
    /// the concrete struct, a direct <c>foreach (var (element, priority) in queue)</c> binds it
    /// without boxing.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Snapshot semantics: the enumerator is built over a <see cref="ToArray"/>
    /// snapshot taken when this method is called, so it is unaffected by enqueues or dequeues that
    /// happen afterward; iteration sees exactly the elements captured by the snapshot, in no
    /// particular order. As with <see cref="ToArray"/>, the snapshot is weakly consistent across
    /// sub-queues and carries no priority ordering.
    /// </para>
    /// <para>
    /// <b>Thread Safety:</b> Constructing the enumerator is thread-safe; the snapshot it iterates is
    /// a private copy, so iterating it never interacts with concurrent mutation.
    /// </para>
    /// </remarks>
    public Enumerator GetEnumerator() => new(ToArray());

    /// <inheritdoc/>
    /// <remarks>
    /// This explicit implementation boxes the value-type <see cref="Enumerator"/> to satisfy the
    /// interface, expected and harmless on the rare LINQ/interface enumeration path. A direct
    /// <c>foreach</c> binds <see cref="GetEnumerator"/> and avoids the box.
    /// </remarks>
    IEnumerator<(TElement Element, TPriority Priority)> IEnumerable<(TElement Element, TPriority Priority)>.GetEnumerator()
        => GetEnumerator();

    /// <inheritdoc/>
    /// <remarks>
    /// This explicit implementation boxes the value-type <see cref="Enumerator"/> to satisfy the
    /// non-generic interface, expected and harmless. A direct <c>foreach</c> avoids the box.
    /// </remarks>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// A value-type enumerator over an unordered, weakly consistent <see cref="ToArray"/> snapshot
    /// of a <see cref="ConcurrentPriorityQueue{TElement, TPriority}"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The enumerator holds a private snapshot array captured at construction, so it is decoupled
    /// from the live queue: subsequent enqueues and dequeues never affect what it yields, and it
    /// never takes a sub-queue lock during iteration. Being a struct, it is bound directly by a
    /// <c>foreach</c> against the concrete <see cref="GetEnumerator"/> result, which avoids the
    /// boxing that the explicit <see cref="IEnumerable{T}"/> interface path incurs.
    /// </para>
    /// <para>
    /// The entries are unordered (no priority ordering), matching the
    /// <see cref="PriorityQueue{TElement, TPriority}.UnorderedItems"/> precedent.
    /// </para>
    /// </remarks>
    public struct Enumerator : IEnumerator<(TElement Element, TPriority Priority)>
    {
        private readonly (TElement Element, TPriority Priority)[] _snapshot;
        private int _index;

        /// <summary>
        /// Initializes a new instance of the <see cref="Enumerator"/> struct over the supplied
        /// snapshot array. The index starts before the first element, so the first
        /// <see cref="MoveNext"/> advances to position zero.
        /// </summary>
        /// <param name="snapshot">The captured, unordered snapshot to iterate.</param>
        internal Enumerator((TElement Element, TPriority Priority)[] snapshot)
        {
            _snapshot = snapshot;
            _index = -1;
        }

        /// <summary>Gets the entry at the enumerator's current position.</summary>
        /// <value>
        /// The <c>(element, priority)</c> entry at the current position. Behavior is undefined before
        /// the first <see cref="MoveNext"/> or after it has returned <see langword="false"/>, matching
        /// the <see cref="IEnumerator{T}.Current"/> contract.
        /// </value>
        public readonly (TElement Element, TPriority Priority) Current => _snapshot[_index];

        /// <inheritdoc/>
        readonly object IEnumerator.Current => Current;

        /// <summary>
        /// Advances the enumerator to the next entry of the snapshot.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if the enumerator advanced to a valid entry;
        /// <see langword="false"/> if it has passed the end of the snapshot.
        /// </returns>
        public bool MoveNext()
        {
            int next = _index + 1;
            if (next >= _snapshot.Length)
            {
                return false;
            }

            _index = next;
            return true;
        }

        /// <summary>
        /// Resets the enumerator to its initial position, before the first entry of the snapshot.
        /// </summary>
        public void Reset() => _index = -1;

        /// <summary>
        /// Releases the resources used by the enumerator. The snapshot is plain managed memory, so
        /// this is a no-op; the method exists only to satisfy <see cref="IDisposable"/>.
        /// </summary>
        public readonly void Dispose()
        {
        }
    }
}
