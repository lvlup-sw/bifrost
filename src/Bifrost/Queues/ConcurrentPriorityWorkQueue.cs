// =============================================================================
// <copyright file="ConcurrentPriorityWorkQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

using Bifrost.Concurrency;

namespace Bifrost.Queues;

/// <summary>
/// The MultiQueue concurrent-priority binding of <see cref="IWorkQueue{T}"/> (DR-4):
/// a bounded <see cref="ConcurrentPriorityQueue{TElement, TPriority}"/> ordered by the
/// WFQ virtual-time key (<see cref="PriorityKey"/>, DR-5), composed with an
/// item-counting <see cref="SemaphoreSlim"/> wake-up. The orchestrator selects this
/// binding when class-based priority dispatch is enabled with the lock-free MultiQueue
/// strategy (T18's <c>DispatchStrategy.PriorityMultiQueue</c>); the default strategy
/// remains <see cref="FifoChannelWorkQueue{T}"/>.
/// </summary>
/// <typeparam name="TWork">The type of work item carried by each envelope.</typeparam>
/// <remarks>
/// <para>
/// <b>Semaphore-counts-items invariant.</b> The semaphore's permit count tracks the
/// number of accepted-but-unclaimed items: <see cref="TryEnqueue(in WorkEnvelope{TWork})"/>
/// releases exactly one permit per item the priority queue ACCEPTED (a capacity or
/// post-completion rejection never releases), and each successful
/// <see cref="WaitToDequeueAsync(CancellationToken)"/> consumes exactly one. N accepted
/// items therefore fund exactly N successful waits — no lost or phantom wake-ups.
/// </para>
/// <para>
/// <b>Relaxed ordering (DR-5).</b> Dequeue order respects the virtual-time key only
/// approximately: the MultiQueue's two-choice <c>TryDequeue</c> removes an element with
/// an expected rank error of <c>(5/6)·n</c> (n = sub-queue count ≈ 4 × processor count),
/// a relaxation that rides on top of the total order the key defines and is acceptable
/// dispatch-ordering noise per design DR-5. Correctness must never depend on how close
/// to the true minimum a pop lands; the starvation bound comes from the bounded boost
/// window in the key itself, not from dequeue exactness.
/// </para>
/// <para>
/// <b>Completion wake mechanism.</b> <see cref="Complete"/> must wake consumers parked
/// on the semaphore of an empty queue (the no-hang property) without disturbing the
/// permit accounting of residual items. It does so by releasing one permit per
/// registered waiter: <see cref="WaitToDequeueAsync(CancellationToken)"/> registers
/// itself (an <see cref="Interlocked"/> increment, a full fence) BEFORE reading the
/// completed flag, and <see cref="Complete"/> publishes the flag (with a full fence)
/// BEFORE reading the waiter count — a store-buffering handshake guaranteeing that a
/// waiter either observes completion and never parks, or is observed by
/// <see cref="Complete"/> and receives a wake-up permit. Never neither, so no consumer
/// is left parked forever. After completion the semaphore is no longer authoritative
/// (residual items may be drained directly via <see cref="TryDequeue(out WorkEnvelope{TWork})"/>
/// without consuming permits), so the wait degrades to a poll: <c>true</c> while
/// residual items remain, <c>false</c> once completed AND empty. Over-released permits
/// from this handshake are harmless: a woken waiter that finds the queue completed and
/// empty reports shutdown, and post-completion waits never park.
/// </para>
/// <para>
/// <see cref="Complete"/> is a concrete-only member — it is not part of
/// <see cref="IWorkQueue{T}"/>; the orchestrator reaches it via the concrete type,
/// mirroring <see cref="FifoChannelWorkQueue{T}"/>. This binding has no producer-wait
/// accept path: producers that need backpressure observe <c>TryEnqueue == false</c>.
/// </para>
/// </remarks>
internal sealed class ConcurrentPriorityWorkQueue<TWork> : IWorkQueue<WorkEnvelope<TWork>>
{
    private readonly ConcurrentPriorityQueue<WorkEnvelope<TWork>, long> _queue;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly PriorityKey.Boosts _boosts;

    /// <summary>
    /// The number of consumers currently inside
    /// <see cref="WaitToDequeueAsync(CancellationToken)"/>. One side of the completion
    /// handshake: incremented (full fence) before the completed flag is read, so
    /// <see cref="Complete"/> can fund one wake-up permit per registered waiter.
    /// </summary>
    private int _waiterCount;

    /// <summary>
    /// Idempotency gate for <see cref="Complete"/>: the first caller flips it and
    /// performs the completion wake; later calls are no-ops.
    /// </summary>
    private int _completedGate;

    /// <summary>
    /// Whether the queue has been completed for adding. Volatile: read on the enqueue
    /// and wait paths, published by <see cref="Complete"/> ahead of a full fence.
    /// </summary>
    private volatile bool _completed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentPriorityWorkQueue{TWork}"/> class.
    /// </summary>
    /// <param name="capacity">The bounded capacity of the queue.</param>
    /// <param name="options">The priority dispatch options defining the class boost windows.</param>
    /// <param name="timestampFrequency">
    /// The timestamp frequency, in units per second, of the <see cref="TimeProvider"/>
    /// that stamps <see cref="WorkEnvelope{TWork}.EnqueuedAtTicks"/> — i.e. its
    /// <see cref="TimeProvider.TimestampFrequency"/>. The boost windows are converted to
    /// these units exactly once here (<see cref="PriorityKey.Precompute"/>), keeping the
    /// per-item key computation pure <see cref="long"/> arithmetic.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="capacity"/> is less than one, or
    /// <paramref name="timestampFrequency"/> is zero or negative.
    /// </exception>
    public ConcurrentPriorityWorkQueue(int capacity, PriorityDispatchOptions options, long timestampFrequency)
    {
        _boosts = PriorityKey.Precompute(options, timestampFrequency);
        _queue = new ConcurrentPriorityQueue<WorkEnvelope<TWork>, long>(capacity);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The MultiQueue sums lock-striped per-sub-queue counters without locking, so the
    /// value is approximate under concurrency and exact at quiescence — exactly the
    /// contract's documented bounds.
    /// </remarks>
    public int Count => _queue.Count;

    /// <inheritdoc/>
    /// <remarks>
    /// Computes the virtual-time key from the envelope (<see cref="PriorityKey.Compute{TWork}"/>)
    /// and offers it to the bounded priority queue. <c>false</c> means hard capacity
    /// exhaustion or a completed queue; a rejection never releases a wake-up permit, so
    /// the semaphore-counts-items invariant is preserved. Class watermarks are a later
    /// admission-policy layer (T21), not part of this binding.
    /// </remarks>
    public bool TryEnqueue(in WorkEnvelope<TWork> item)
    {
        if (_completed)
        {
            // Mirrors the FIFO binding's completed-channel behavior: rejection via false,
            // never an exception.
            return false;
        }

        if (!_queue.TryEnqueue(item, PriorityKey.Compute(in item, in _boosts)))
        {
            // Hard capacity: the bounded gate rejected the item — no permit is released.
            return false;
        }

        // Semaphore counts ITEMS: exactly one permit per accepted item.
        _signal.Release();
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Cancellation completes the wait with <c>false</c> — never an
    /// <see cref="OperationCanceledException"/> — per the contract's shutdown semantic.
    /// After <see cref="Complete"/>, the wait completes <c>false</c> once the queue is
    /// also empty; while residual items remain it completes <c>true</c> so the canonical
    /// consume loop can drain them (see the completion-wake remarks on the class).
    /// </remarks>
    public async ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken)
    {
        // Register as a waiter BEFORE reading the completed flag. Complete() publishes
        // the flag (full fence) before reading this count, so either this method sees
        // completion here and never parks, or Complete() sees the registration and funds
        // a wake-up permit — never neither (store-buffering handshake).
        Interlocked.Increment(ref _waiterCount);
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Contract shutdown semantic: report false rather than throwing.
                return false;
            }

            if (_completed)
            {
                // Post-completion the semaphore is no longer authoritative (residual
                // items may be drained directly without consuming permits), so the wait
                // degrades to a poll: true while residual items remain, else false.
                return !_queue.IsEmpty;
            }

            try
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Contract shutdown semantic: cancellation completes the wait with false.
                return false;
            }

            // Permit acquired: either an item permit (an item is likely available —
            // TryDequeue may still spuriously miss; consumers loop back to this wait) or
            // a completion wake-up (completed AND empty: report shutdown).
            return !_completed || !_queue.IsEmpty;
        }
        finally
        {
            Interlocked.Decrement(ref _waiterCount);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Delegates to the MultiQueue's relaxed two-choice dequeue: the removed envelope has
    /// one of the smallest virtual-time keys, within the documented <c>(5/6)·n</c>
    /// expected rank error (acceptable dispatch noise per DR-5). A <c>false</c> may be
    /// spurious under concurrent mutation — consumers loop back to
    /// <see cref="WaitToDequeueAsync(CancellationToken)"/> per the canonical consume loop.
    /// </remarks>
    public bool TryDequeue([MaybeNullWhen(false)] out WorkEnvelope<TWork> item)
        => _queue.TryDequeue(out item, out _);

    /// <summary>
    /// Marks the queue as complete for adding (concrete-only member, mirroring
    /// <see cref="FifoChannelWorkQueue{T}.Complete"/>). Residual items remain dequeueable
    /// via <see cref="TryDequeue(out WorkEnvelope{TWork})"/> or the canonical consume
    /// loop; once drained, <see cref="WaitToDequeueAsync(CancellationToken)"/> completes
    /// <c>false</c>. Wakes every consumer currently parked on the semaphore by releasing
    /// one permit per registered waiter (the no-hang handshake — see the class remarks).
    /// Idempotent: only the first call performs the wake.
    /// </summary>
    public void Complete()
    {
        if (Interlocked.Exchange(ref _completedGate, 1) == 1)
        {
            return;
        }

        _completed = true;

        // Full fence between publishing the flag and reading the waiter count: pairs
        // with the Interlocked.Increment in WaitToDequeueAsync so a waiter that missed
        // the flag is always visible here and receives a wake-up permit.
        Interlocked.MemoryBarrier();

        var waiters = Volatile.Read(ref _waiterCount);
        if (waiters > 0)
        {
            _signal.Release(waiters);
        }
    }
}
