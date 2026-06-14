// =============================================================================
// <copyright file="ConcurrentPriorityQueue.Enqueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Concurrency;

/// <content>
/// The enqueue surface: random sub-queue selection with try-lock resampling that never
/// blocks. A push draws a random sub-queue index from the thread-local RNG and attempts the
/// sub-queue's writer lock; on contention it resamples a <i>fresh</i> random index and retries,
/// rather than waiting on a held lock or spinning on the same queue. Because at most <c>p</c> of
/// the <c>n = 4p</c> sub-queues can be locked simultaneously, an unlocked queue always exists, so
/// the resample loop terminates with an expected lock-acquisition count barely above one; no
/// attempt bound is needed.
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
    /// Unbounded queues always accept the element and this method always returns normally.
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
    /// Attempts to add an element with the specified priority to the queue without ever blocking.
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
        bool reserved = false;

        // Bounded-capacity reservation, taken atomically ahead of the resample loop. Guarded
        // by `_boundedCapacity > 0` so the unbounded path executes no Interlocked instruction at all.
        if (_boundedCapacity > 0)
        {
            if (Interlocked.Increment(ref _boundedCount) > _boundedCapacity)
            {
                // Over the bound: exactly undo the reservation so a failed TryEnqueue leaks nothing,
                // and report "full" without touching any sub-queue.
                Interlocked.Decrement(ref _boundedCount);
                return false;
            }

            reserved = true;
        }

        ThreadHandle handle = ThreadHandle.Current;
        SubQueue<TElement, TPriority>[] queues = _queues;

        try
        {
            while (true)
            {
                int index = handle.NextStickyIndex(_subQueueMask, _stickiness);
                if (queues[index].TryLockedPush(element, priority))
                {
                    return true;
                }

                // Contended: end the sticky period so the resample draws a fresh random index rather than
                // re-trying the contended one; never wait on a held lock. An unlocked sub-queue
                // always exists (at most p of n = 4p can be locked at once), so this loop terminates
                // probabilistically without an attempt bound. Resampling-on-contention is also what keeps
                // stickiness wait-free: a stuck selection never blocks, it yields to a fresh draw.
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
