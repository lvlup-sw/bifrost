// =============================================================================
// <copyright file="ConcurrentPriorityQueue.Enqueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Concurrency;

/// <content>
/// Random insertion routing with try-lock resampling. Contention retries are unbounded;
/// the processor-based stripe count does not bound the number of participating threads.
/// </content>
public sealed partial class ConcurrentPriorityQueue<TElement, TPriority>
{
    /// <summary>
    /// Adds an element with the specified priority to the queue.
    /// </summary>
    /// <param name="element">
    /// The element to enqueue. May be <see langword="null"/> when <typeparamref name="TElement"/>
    /// is a nullable type; elements flow through opaquely and only the priority is ordered
    /// (the <see cref="PriorityQueue{TElement, TPriority}"/> precedent).
    /// </param>
    /// <param name="priority">The priority that orders the element; passed to the queue's comparer.</param>
    /// <remarks>
    /// <para>
    /// Unbounded queues have no capacity rejection; comparer exceptions still propagate.
    /// </para>
    /// <para>
    /// Bounded queues (constructed with a positive <c>boundedCapacity</c>) throw
    /// <see cref="InvalidOperationException"/> when the queue is full; use
    /// <see cref="TryEnqueue"/> for the non-throwing variant. The bounded-capacity gate
    /// reserves a slot atomically before any sub-queue is touched, so a full queue is rejected
    /// without mutating any stripe.
    /// </para>
    /// <para>
    /// <b>Thread Safety:</b> This method is thread-safe and may be called concurrently from
    /// multiple threads. It scatters across the lock-striped sub-queues and never blocks on a
    /// contended sub-queue: a contended lock triggers a fresh random resample.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The queue is bounded (constructed with a positive <c>boundedCapacity</c>) and full.
    /// </exception>
    public void Enqueue(TElement element, TPriority priority)
    {
        if (!TryEnqueue(element, priority))
        {
            // Reachable only on a bounded, full queue: the reservation gate rejected the element.
            // On an unbounded queue TryEnqueue always returns true and this never throws.
            throw new InvalidOperationException("The queue is full and cannot accept additional elements.");
        }
    }

    /// <summary>
    /// Attempts to add an element with the specified priority to the queue using try-lock resampling. Completion time is unbounded.
    /// </summary>
    /// <param name="element">
    /// The element to enqueue. May be <see langword="null"/> when <typeparamref name="TElement"/>
    /// is a nullable type (the <see cref="PriorityQueue{TElement, TPriority}"/> precedent).
    /// </param>
    /// <param name="priority">The priority that orders the element; passed to the queue's comparer.</param>
    /// <returns>
    /// <see langword="true"/> when the element was added. On an unbounded queue this is always
    /// <see langword="true"/>; on a bounded queue (constructed with a positive
    /// <c>boundedCapacity</c>) it is <see langword="false"/> when the queue is full.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Bounded queues reserve a slot through the shared atomic capacity gate
    /// <i>before</i> any sub-queue is touched: a failed reservation returns <see langword="false"/>
    /// without mutating any stripe and never leaks capacity (the reservation is exactly undone).
    /// </para>
    /// <para>
    /// <b>Thread Safety:</b> This method is thread-safe and may be called concurrently from
    /// multiple threads. It scatters across the lock-striped sub-queues and never blocks on a
    /// contended sub-queue: a contended lock triggers a fresh random resample.
    /// </para>
    /// </remarks>
    public bool TryEnqueue(TElement element, TPriority priority)
        => EnqueueCore(element, priority);

    /// <summary>
    /// Scatters an element into a random sub-queue, resampling a fresh random index whenever the
    /// chosen sub-queue's lock is contended. This is the single mutation entry point that
    /// both public enqueue methods funnel through.
    /// </summary>
    /// <param name="element">The element to store.</param>
    /// <param name="priority">The priority that orders the element.</param>
    /// <returns>
    /// <see langword="true"/> when the element was stored; <see langword="false"/> when the queue is
    /// bounded and the reservation gate found it full (the unbounded path always returns
    /// <see langword="true"/>).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Bounded-capacity gate: a single
    /// <see cref="System.Threading.Interlocked"/>-based reservation is taken <i>before</i> the
    /// resample loop: a successful reservation guarantees a slot and the loop proceeds unchanged; a
    /// failed reservation is exactly undone (so nothing leaks) and "full" is reported without
    /// touching any sub-queue. The gate is the only mutation entry point both public enqueue methods
    /// funnel through.
    /// </para>
    /// <para>
    /// The unbounded path executes no atomic: the reservation is guarded by
    /// <c>_boundedCapacity &gt; 0</c> over the readonly <c>_boundedCapacity</c> field, so an
    /// unbounded queue (<c>_boundedCapacity &lt; 0</c>) runs no
    /// <see cref="System.Threading.Interlocked"/> instruction against the shared gate and the loop
    /// is unchanged from the unbounded design.
    /// </para>
    /// </remarks>
    private bool EnqueueCore(TElement element, TPriority priority)
    {
        // Resolve thread-local state before reserving capacity: initialization failure cannot
        // leak a reservation, and the retry loop does not need initialization inside its try.
        ThreadHandle handle = ThreadHandle.Current;
        SubQueue<TElement, TPriority>[] queues = _queues;
        bool reserved = false;

        // Bounded-capacity reservation, taken atomically ahead of the resample loop. Guarded
        // by `_boundedCapacity > 0` so the unbounded path executes no Interlocked instruction at all.
        if (_boundedCapacity > 0)
        {
            int count;
            do
            {
                count = Volatile.Read(ref _boundedCount);
                if (count >= _boundedCapacity) return false;
            }

            while (Interlocked.CompareExchange(ref _boundedCount, count + 1, count) != count);

            reserved = true;
        }

        try
        {
            while (true)
            {
                int index = handle.NextStickyIndex(_subQueueMask, _stickiness);
                if (queues[index].TryLockedPush(element, priority))
                {
                    return true;
                }

                // Contention ends the sticky period and draws a fresh random index.
                // This retry loop has no per-operation progress bound.
                handle.ResetStickyEnqueue();
            }
        }
        catch
        {
            // A reservation was taken but the element never landed in a sub-queue (e.g. a comparer or
            // heap-path throw inside TryLockedPush). Release it so a transient failure can't permanently
            // shrink a bounded queue's usable capacity by leaking `_boundedCount`.
            if (reserved)
            {
                Interlocked.Decrement(ref _boundedCount);
            }

            throw;
        }
    }
}
