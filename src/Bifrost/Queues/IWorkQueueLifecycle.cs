// =============================================================================
// <copyright file="IWorkQueueLifecycle.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Queues;

/// <summary>
/// Lifecycle hooks the orchestrator drives on its work queue: completion for adding, the
/// retirement wake hand-off, and the matched-wake take of the greedy consume loop.
/// </summary>
/// <remarks>
/// <para>
/// Every queue the orchestrator owns implements this contract next to
/// <see cref="IWorkQueue{T}"/>, so the orchestrator never pattern-matches on concrete bindings.
/// A binding without a wake semaphore (the FIFO channel binding) implements
/// <see cref="WakeConsumer"/> and <see cref="ConsumeMatchedWake"/> as no-ops.
/// </para>
/// <para>
/// <b>Wake accounting.</b> A binding funds one wake permit for each item a consumer can take. A
/// leased binding (one whose dequeue hands out a lease that is released later) funds wakes on
/// dispatchability, not on admission: an admitted item that cannot be dispatched yet does not
/// release a permit until it becomes dispatchable.
/// </para>
/// <para>
/// <b>After completion.</b> The binding's semaphore stays authoritative after
/// <see cref="Complete"/>. A wait must not fall back to polling for a positive count, because a
/// leased binding can hold items that are admitted but not dispatchable.
/// </para>
/// </remarks>
internal interface IWorkQueueLifecycle
{
    /// <summary>
    /// Marks the queue complete for adding. Residual items stay dequeueable; once they are
    /// drained, the wait completes with <c>false</c>.
    /// </summary>
    /// <remarks>
    /// The orchestrator calls this at most once, under its lifecycle gate. Bindings still make
    /// the call idempotent.
    /// </remarks>
    void Complete();

    /// <summary>
    /// Restores one wake permit after a retiring worker exits.
    /// </summary>
    /// <remarks>
    /// A retiring worker can win a permit and then exit without taking the item it funded. The
    /// orchestrator calls this from the worker's exit path whenever retirement was requested, so
    /// it can also add a permit that funds nothing. A binding must tolerate that surplus permit:
    /// the extra wake ends in a dequeue miss, and the consume loop goes back to its wait.
    /// </remarks>
    void WakeConsumer();

    /// <summary>
    /// Takes the permit that funded an item the greedy inner consume loop dequeued without a wait.
    /// </summary>
    /// <remarks>
    /// Without this take, a drain leaves surplus permits and later waits spin on an empty queue.
    /// Once the binding is completed and empty, it must not take a permit: completion releases
    /// the permits that end parked waits, and taking one strands a parked consumer. The check
    /// must run under the same lock that <see cref="Complete"/> uses to publish its flag and
    /// release those permits, as <see cref="LockingPriorityWorkQueue{TWork}"/> does.
    /// </remarks>
    void ConsumeMatchedWake();
}
