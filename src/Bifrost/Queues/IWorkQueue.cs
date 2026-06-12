// =============================================================================
// <copyright file="IWorkQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

namespace Bifrost.Queues;

/// <summary>
/// Strategy contract for the orchestrator's pluggable work queue.
/// </summary>
/// <typeparam name="T">The type of item held by the queue.</typeparam>
/// <remarks>
/// <para>
/// Bindings compose a try-based queue structure with a count-semaphore wake-up:
/// producers <see cref="TryEnqueue(in T)"/> and release a permit; consumers
/// <see cref="WaitToDequeueAsync(CancellationToken)"/> for a permit and then
/// <see cref="TryDequeue(out T)"/>.
/// </para>
/// <para>
/// <b>Canonical consume loop.</b> Under relaxed (lock-free or striped) bindings,
/// <see cref="TryDequeue(out T)"/> may spuriously miss even immediately after a
/// successful wait. Consumers must therefore loop: await the wait; on <c>true</c>,
/// attempt the dequeue; on a miss, loop back to the wait. A miss is never an error.
/// </para>
/// <para>
/// <b>Shutdown semantic.</b> Cancellation of the token passed to
/// <see cref="WaitToDequeueAsync(CancellationToken)"/> is the orderly shutdown signal:
/// the wait completes with <c>false</c> — it never throws
/// <see cref="OperationCanceledException"/>. After a <c>false</c> result, residual
/// items may still be drained directly via <see cref="TryDequeue(out T)"/>.
/// </para>
/// <para>
/// <b>Admission policy.</b> Watermarks, class-based admission, and any other
/// acceptance rules are binding concerns surfaced uniformly as
/// <see cref="TryEnqueue(in T)"/> returning <c>false</c>; callers cannot distinguish
/// capacity exhaustion from policy rejection through this contract.
/// </para>
/// </remarks>
public interface IWorkQueue<T>
{
    /// <summary>
    /// Gets the number of items currently in the queue.
    /// </summary>
    /// <value>
    /// Approximate under concurrency: bindings may use striped or relaxed counters, so
    /// the value can lag in-flight operations. Exact at quiescence: with no concurrent
    /// producers or consumers, the value equals the true item count.
    /// </value>
    int Count { get; }

    /// <summary>
    /// Attempts to enqueue an item without blocking.
    /// </summary>
    /// <param name="item">The item to enqueue.</param>
    /// <returns>
    /// <c>true</c> if the item was accepted; <c>false</c> if the queue is at capacity
    /// or the binding's admission policy (for example, class watermarks) rejected the
    /// item. The two causes are intentionally indistinguishable to callers.
    /// </returns>
    bool TryEnqueue(in T item);

    /// <summary>
    /// Waits until an item is likely available to dequeue, or until shutdown.
    /// </summary>
    /// <param name="cancellationToken">
    /// Token whose cancellation signals orderly shutdown of the consumer.
    /// </param>
    /// <returns>
    /// <c>true</c> when an item is likely available — follow with
    /// <see cref="TryDequeue(out T)"/>, tolerating a spurious miss by looping back to
    /// this wait. <c>false</c> on shutdown: this method completes with <c>false</c>
    /// when <paramref name="cancellationToken"/> is cancelled and never throws
    /// <see cref="OperationCanceledException"/>.
    /// </returns>
    ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to dequeue an item without blocking.
    /// </summary>
    /// <param name="item">The dequeued item when the method returns <c>true</c>.</param>
    /// <returns>
    /// <c>true</c> if an item was dequeued; <c>false</c> on a miss. Relaxed bindings
    /// may miss spuriously even directly after a successful
    /// <see cref="WaitToDequeueAsync(CancellationToken)"/> — on a miss, loop back to
    /// the wait rather than treating it as an error or busy-spinning.
    /// </returns>
    bool TryDequeue([MaybeNullWhen(false)] out T item);
}
