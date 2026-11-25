// =============================================================================
// <copyright file="ILeasedWorkQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

namespace Bifrost.Queues;

/// <summary>
/// Optional dequeue path for a queue that hands out a lease with each item and must learn when
/// the orchestrator has finished with that item.
/// </summary>
/// <typeparam name="T">The type of item held by the queue.</typeparam>
/// <remarks>
/// <para>
/// This contract does not derive from <see cref="IWorkQueue{T}"/>. A binding implements it next
/// to <see cref="IWorkQueue{T}"/> and <see cref="IWorkQueueLifecycle"/>, and the orchestrator
/// detects it once at construction. When it is present, every worker dequeue (the outer consume
/// loop, the greedy inner loop, and the retired-worker drain) goes through
/// <see cref="TryDequeue(out T, out object?)"/> instead of <see cref="IWorkQueue{T}.TryDequeue(out T)"/>.
/// Only <see cref="KeyedWorkQueue{TWork, TKey}"/> implements it; the FIFO and priority bindings do not.
/// </para>
/// <para>
/// A leased binding follows these rules:
/// </para>
/// <list type="bullet">
/// <item><description>
/// It funds wake permits on dispatchability, not on admission. An admitted item that cannot be
/// dispatched yet releases no permit until it becomes dispatchable, for example when
/// <see cref="Release"/> frees the slot it waits for.
/// </description></item>
/// <item><description>
/// Its semaphore stays authoritative after <see cref="IWorkQueueLifecycle.Complete"/>. A wait
/// must not fall back to polling for a positive <see cref="IWorkQueue{T}.Count"/>, because admitted
/// items that are not dispatchable still count.
/// </description></item>
/// <item><description>
/// <see cref="IWorkQueueLifecycle.ConsumeMatchedWake"/> takes no permit once the binding is
/// completed and empty. The check runs under the binding's lock, as
/// <see cref="LockingPriorityWorkQueue{TWork}.ConsumeMatchedWake"/> does.
/// </description></item>
/// <item><description>
/// It tolerates a surplus permit from the retirement hand-off
/// (<see cref="IWorkQueueLifecycle.WakeConsumer"/>): the extra wake ends in a dequeue miss.
/// </description></item>
/// <item><description>
/// It receives exactly one <see cref="Release"/> per leased dequeue, after the handler pipeline
/// for that item has exited.
/// </description></item>
/// <item><description>
/// <see cref="DiscardQueued"/> removes queued items without a lease, so an abandonment sweep
/// never adds a release.
/// </description></item>
/// </list>
/// <para>
/// The test-only <c>WorkOrchestrator.TryReadEnvelope</c> reads through the plain
/// <see cref="IWorkQueue{T}.TryDequeue(out T)"/>, so it is valid only for bindings that return no lease.
/// </para>
/// </remarks>
internal interface ILeasedWorkQueue<T>
{
    /// <summary>Attempts to dequeue an item and the lease that goes with it, without blocking.</summary>
    /// <param name="item">The dequeued item when the method returns <c>true</c>.</param>
    /// <param name="lease">
    /// The lease for <paramref name="item"/>, or <see langword="null"/> when the item needs no
    /// release. Always <see langword="null"/> when the method returns <c>false</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if an item was dequeued; <c>false</c> on a miss. As with
    /// <see cref="IWorkQueue{T}.TryDequeue(out T)"/>, a miss directly after a successful wait is
    /// not an error, and the consume loop goes back to its wait.
    /// </returns>
    bool TryDequeue([MaybeNullWhen(false)] out T item, out object? lease);

    /// <summary>Returns a lease that <see cref="TryDequeue(out T, out object?)"/> handed out.</summary>
    /// <param name="lease">A non-null lease from a dequeue that has not been released yet.</param>
    /// <remarks>
    /// <para>
    /// The orchestrator calls this exactly once per non-null lease, from the worker that dequeued
    /// the item. The call comes after the whole handler pipeline for that item has exited: the
    /// decorators, every dead-letter retry, and the disposal of a scoped handler's DI scope. It
    /// comes on success, on a handler failure, and on cancellation alike. It comes before the
    /// worker reports itself idle, so the autoscaler never sees an idle worker that still holds a
    /// lease.
    /// </para>
    /// <para>
    /// An exception from this method does not leave the worker marked busy: the idle report runs
    /// in a <c>finally</c> block around it. The exception then follows the existing worker error
    /// policy: the worker logs it, stops, and the orchestrator's completion faults with it.
    /// </para>
    /// <para>
    /// One path differs. When retirement cancels a worker's wait, the worker drains retained items
    /// from inside its cancellation <c>catch</c> block. An exception thrown there skips the
    /// sibling error <c>catch</c>, so it is not logged and does not fault completion; only the
    /// worker's own task faults. A binding should therefore not rely on a throwing release to
    /// surface a stuck slot.
    /// </para>
    /// </remarks>
    void Release(object lease);

    /// <summary>
    /// Removes every queued item without reserving a slot, after <see cref="IWorkQueueLifecycle.Complete"/>,
    /// and adds the removed items to <paramref name="discarded"/>.
    /// </summary>
    /// <param name="discarded">Receives the removed items, oldest first within each key.</param>
    /// <returns>The number of removed items.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the binding is not completed yet.</exception>
    /// <remarks>
    /// <para>
    /// This is the leased half of the orchestrator's abandonment sweep, which runs when
    /// <c>AbortAsync</c> starts and when the last worker exits. It removes queued items only,
    /// including items blocked behind a running item of the same key. It hands out no lease and
    /// never touches a slot that a running item holds, so a sweep cannot release a slot against
    /// a handler that is still running or still in its <c>finally</c> block. That handler's own
    /// <see cref="Release"/> stays the only release of its slot.
    /// </para>
    /// <para>
    /// A key with no running items left is removed. When the sweep leaves the completed binding
    /// empty, the binding wakes its parked waiters so that they see the end of the queue.
    /// </para>
    /// </remarks>
    int DiscardQueued(ICollection<T> discarded);
}
