// =============================================================================
// <copyright file="ConcurrentPriorityQueue.Count.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry/Concurrency/MultiQueue/ConcurrentPriorityQueue.Count.cs)

using Bifrost.Concurrency.MultiQueue;

namespace Bifrost.Concurrency;

/// <content>
/// The occupancy surface: a snapshot-semantics <see cref="Count"/> and a short-circuiting
/// <see cref="IsEmpty"/> over the lock-striped sub-queue counts. Both read each stripe's
/// <c>VolatileCount</c> snapshot without taking any sub-queue lock; they observe a momentary,
/// possibly-stale picture of a queue that other threads may be mutating concurrently, the same
/// relaxed guarantee that <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/> offers.
/// </content>
public sealed partial class ConcurrentPriorityQueue<TElement, TPriority>
{
    /// <summary>
    /// Gets the number of elements contained in the queue.
    /// </summary>
    /// <value>The number of elements contained in the queue.</value>
    /// <remarks>
    /// <para>
    /// Snapshot semantics: the count is the sum of the lock-striped per-sub-queue volatile
    /// counts read without any lock, so it is a momentary snapshot of a queue that may be mutated
    /// by other threads concurrently; by the time the value is returned the queue may already hold
    /// a different number of elements (the
    /// <see cref="System.Collections.Concurrent.ConcurrentQueue{T}.Count"/> precedent). The stripe
    /// reads can interleave with concurrent pops, so the raw sum is clamped at zero: a transient
    /// interleaving never surfaces a negative count. The same non-atomic stripe scan can also
    /// transiently OVER-count by a small amount under concurrent mutation; on a bounded queue a
    /// momentary reading slightly above <see cref="BoundedCapacity"/> is possible and does not
    /// indicate a capacity violation (the reservation gate, not this snapshot, enforces the bound;
    /// at quiescence the count is exact).
    /// </para>
    /// <para>
    /// For an emptiness check prefer <see cref="IsEmpty"/> over comparing <see cref="Count"/> to
    /// zero, both because it can short-circuit at the first non-empty stripe and because it gives a
    /// more reliable momentary answer under concurrent mutation (the
    /// <see cref="System.Collections.Concurrent.ConcurrentQueue{T}.IsEmpty"/> precedent).
    /// </para>
    /// <para>
    /// <b>Thread Safety:</b> This property is thread-safe and may be read concurrently from multiple
    /// threads while the queue is being mutated; it never blocks on a sub-queue lock.
    /// </para>
    /// </remarks>
    public int Count
    {
        get
        {
            SubQueue<TElement, TPriority>[] queues = _queues;
            long sum = 0;
            for (int i = 0; i < queues.Length; i++)
            {
                sum += queues[i].VolatileCount;
            }

            // Clamp at zero: the unlocked stripe reads can interleave with concurrent pops, so a
            // transient view must never surface a negative count.
            return sum < 0 ? 0 : (int)sum;
        }
    }

    /// <summary>
    /// Gets a value that indicates whether the queue is empty.
    /// </summary>
    /// <value>
    /// <see langword="true"/> if the queue is empty; otherwise, <see langword="false"/>.
    /// </value>
    /// <remarks>
    /// <para>
    /// Snapshot semantics: emptiness is decided by scanning the lock-striped per-sub-queue
    /// volatile counts without any lock and returning <see langword="false"/> at the first non-empty
    /// stripe, cheaper than summing every stripe, and the preferred emptiness check. The result is
    /// a momentary snapshot of a queue that may be mutated by other threads concurrently (the
    /// <see cref="System.Collections.Concurrent.ConcurrentQueue{T}.IsEmpty"/> precedent).
    /// </para>
    /// <para>
    /// Prefer this property over comparing <see cref="Count"/> to zero: it can short-circuit and
    /// gives a more reliable momentary answer under concurrent mutation.
    /// </para>
    /// <para>
    /// <b>Thread Safety:</b> This property is thread-safe and may be read concurrently from multiple
    /// threads while the queue is being mutated; it never blocks on a sub-queue lock.
    /// </para>
    /// </remarks>
    public bool IsEmpty
    {
        get
        {
            SubQueue<TElement, TPriority>[] queues = _queues;
            for (int i = 0; i < queues.Length; i++)
            {
                // Short-circuit at the first non-empty stripe: cheaper than a full sum.
                if (queues[i].VolatileCount != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
