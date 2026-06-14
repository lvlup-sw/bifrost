// =============================================================================
// <copyright file="IWorkOrchestrator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>
/// Orchestrates work processing through a bounded queue with configurable workers.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <remarks>
/// <para>
/// The work orchestrator provides a high-performance, zero-allocation hot path for
/// enqueueing work items. It manages a pool of workers that process items from the
/// internal queue.
/// </para>
/// <para>
/// Key design principles:
/// <list type="bullet">
///   <item><description>ValueTask-based API for zero-allocation hot path</description></item>
///   <item><description>Non-allocating property access for observability</description></item>
///   <item><description>Admission outcomes surfaced as <see cref="EnqueueResult"/> values, never exceptions</description></item>
/// </list>
/// </para>
/// <para>
/// Admission semantics: enqueue operations report whether the work item was
/// admitted to the queue. Rejection happens at admission — before the item
/// enters the queue — so the at-least-once execution contract is unaffected:
/// admitted work is executed at least once, rejected work was never admitted.
/// Idempotency (e.g., JobName-style deduplication) is a consumer concern; the
/// orchestrator does not deduplicate admitted work.
/// </para>
/// </remarks>
public interface IWorkOrchestrator<TWork> : IAsyncDisposable
{
    /// <summary>
    /// Gets the number of work items currently pending in the queue.
    /// </summary>
    /// <value>The count of pending work items.</value>
    int PendingCount { get; }

    /// <summary>
    /// Gets the number of currently active workers processing items.
    /// </summary>
    /// <value>The count of active workers.</value>
    int ActiveWorkers { get; }

    /// <summary>
    /// Gets the maximum capacity of the internal queue.
    /// </summary>
    /// <value>The queue capacity.</value>
    int Capacity { get; }

    /// <summary>
    /// Enqueues a work item for processing asynchronously.
    /// </summary>
    /// <param name="work">The work item to enqueue.</param>
    /// <param name="workClass">
    /// The <see cref="WorkClass"/> the item is enqueued under. Defaults to
    /// <see cref="WorkClass.Default"/>.
    /// </param>
    /// <param name="ct">
    /// Cancellation token for the enqueue operation. If it is canceled, the
    /// operation is canceled and surfaces an <see cref="OperationCanceledException"/>
    /// (the returned task completes in the canceled state) — it is not folded into a
    /// rejected result. See the remarks.
    /// </param>
    /// <returns>
    /// The <see cref="EnqueueResult"/> admission outcome:
    /// <see cref="EnqueueResult.Accepted"/> when the item was admitted, or a
    /// rejected result carrying the <see cref="RejectionReason"/> otherwise.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method may wait asynchronously if the queue is at capacity,
    /// implementing backpressure behavior.
    /// </para>
    /// <para>
    /// <b>Admission vs. cancellation.</b> Admission <i>decisions</i> are values, not
    /// exceptions: a full queue, a tripped per-class watermark, or an orchestrator
    /// shutting down all return <see cref="EnqueueResult.Rejected(RejectionReason)"/>
    /// (with <see cref="RejectionReason.CapacityExceeded"/>,
    /// <see cref="RejectionReason.WatermarkExceeded"/>, or
    /// <see cref="RejectionReason.Shutdown"/> respectively). The method never throws
    /// for an admission failure. <i>Caller cancellation</i> is a different concern:
    /// canceling <paramref name="ct"/> surfaces an
    /// <see cref="OperationCanceledException"/>, matching the Task-based Asynchronous
    /// Pattern and the <see cref="System.Threading.Channels.ChannelWriter{T}"/>
    /// precedent (completion returns "no writes permitted"; cancellation throws). A
    /// canceled enqueue is therefore distinguishable from a shut-down queue and is
    /// never dead-lettered.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was canceled before or during the enqueue.
    /// </exception>
    ValueTask<EnqueueResult> EnqueueAsync(TWork work, WorkClass workClass = WorkClass.Default, CancellationToken ct = default);

    /// <summary>
    /// Attempts to enqueue a work item without blocking.
    /// </summary>
    /// <param name="work">The work item to enqueue.</param>
    /// <param name="workClass">
    /// The <see cref="WorkClass"/> the item is enqueued under. Defaults to
    /// <see cref="WorkClass.Default"/>.
    /// </param>
    /// <returns>True if the item was admitted; false if the queue is full or shut down.</returns>
    /// <remarks>
    /// Use this method when you need non-blocking enqueue behavior and can
    /// handle rejection gracefully.
    /// </remarks>
    bool TryEnqueue(TWork work, WorkClass workClass = WorkClass.Default);

    /// <summary>
    /// Gracefully stops the orchestrator, allowing pending work to complete.
    /// </summary>
    /// <param name="ct">Cancellation token to cancel the stop operation.</param>
    /// <returns>A Task that completes when all workers have stopped.</returns>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Enqueues a work item for processing synchronously.
    /// </summary>
    /// <param name="work">The work item to enqueue.</param>
    /// <param name="workClass">
    /// The <see cref="WorkClass"/> the item is enqueued under. Defaults to
    /// <see cref="WorkClass.Default"/>.
    /// </param>
    /// <exception cref="InvalidOperationException">Thrown when the queue is full.</exception>
    /// <remarks>
    /// This method throws if the queue is at capacity. For non-throwing
    /// behavior, use <see cref="TryRun"/>.
    /// </remarks>
    void Run(TWork work, WorkClass workClass = WorkClass.Default);

    /// <summary>
    /// Attempts to enqueue a work item synchronously without throwing.
    /// </summary>
    /// <param name="work">The work item to enqueue.</param>
    /// <param name="workClass">
    /// The <see cref="WorkClass"/> the item is enqueued under. Defaults to
    /// <see cref="WorkClass.Default"/>.
    /// </param>
    /// <returns>True if the item was admitted; false if the queue is full or shut down.</returns>
    /// <remarks>
    /// Use this method when you need non-throwing synchronous enqueue behavior
    /// and can handle rejection gracefully.
    /// </remarks>
    bool TryRun(TWork work, WorkClass workClass = WorkClass.Default);

    /// <summary>
    /// Creates a worker function that processes work from the internal queue.
    /// </summary>
    /// <returns>A function that can be used to start a worker loop.</returns>
    /// <remarks>
    /// The returned function takes a worker ID and cancellation token, and processes
    /// work items until the queue is completed or cancellation is requested.
    /// </remarks>
    Func<string, CancellationToken, Task> CreateWorkerFunction();

    /// <summary>
    /// Creates a worker function that processes work from the internal queue
    /// with state tracking callback support.
    /// </summary>
    /// <param name="stateCallback">
    /// Optional callback invoked when worker state changes.
    /// Called with <c>true</c> when worker starts processing (busy),
    /// and <c>false</c> when worker completes processing (idle).
    /// </param>
    /// <returns>A function that can be used to start a worker loop with state tracking.</returns>
    /// <remarks>
    /// <para>
    /// The callback is guaranteed to be invoked with <c>false</c> even if the
    /// handler throws an exception, ensuring proper state tracking.
    /// </para>
    /// <para>
    /// This is useful for autoscaling scenarios where the orchestrator needs to
    /// track worker utilization to make scaling decisions.
    /// </para>
    /// </remarks>
    Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback);

    /// <summary>
    /// Requests scaling up by adding the specified number of workers.
    /// </summary>
    /// <param name="count">The number of workers to add.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>A task that completes when the request has been processed.</returns>
    /// <remarks>
    /// <para>
    /// The base implementation is a no-op. Scaling decorators (e.g.,
    /// AutoscalingOrchestrator) override this to provide actual scaling behavior.
    /// </para>
    /// </remarks>
    Task RequestScaleUpAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests scaling down by removing the specified number of workers.
    /// </summary>
    /// <param name="count">The number of workers to remove.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>A task that completes when the request has been processed.</returns>
    /// <remarks>
    /// <para>
    /// The base implementation is a no-op. Scaling decorators (e.g.,
    /// AutoscalingOrchestrator) override this to provide actual scaling behavior.
    /// </para>
    /// </remarks>
    Task RequestScaleDownAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drains the orchestrator by processing all remaining queued items
    /// without accepting new work.
    /// </summary>
    /// <param name="ct">Cancellation token to abort the drain operation.</param>
    /// <returns>A task that completes when all queued items have been processed.</returns>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="StopAsync"/>, which cancels workers immediately,
    /// <c>DrainAsync</c> stops admission (preventing new enqueues) and waits
    /// for workers to finish processing all remaining items naturally.
    /// </para>
    /// <para>
    /// This is useful for zero-downtime deployments where in-flight work should
    /// complete before the host shuts down.
    /// </para>
    /// <para>
    /// After <c>DrainAsync</c> completes:
    /// <list type="bullet">
    ///   <item><description><see cref="EnqueueAsync"/> will return a rejected result with <see cref="RejectionReason.Shutdown"/></description></item>
    ///   <item><description><see cref="TryEnqueue"/> will return <c>false</c></description></item>
    ///   <item><description><see cref="PendingCount"/> will be 0</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// This method is idempotent: calling it multiple times after the first drain completes
    /// immediately with no side effects.
    /// </para>
    /// </remarks>
    Task DrainAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the shutdown token for the orchestrator.
    /// </summary>
    /// <returns>A cancellation token that is canceled when the orchestrator is stopped or disposed.</returns>
    /// <remarks>
    /// <para>
    /// This token can be used by dynamic workers to detect when the orchestrator
    /// is shutting down and perform graceful cleanup.
    /// </para>
    /// <para>
    /// The token is canceled during both <see cref="StopAsync"/> and
    /// <see cref="IAsyncDisposable.DisposeAsync"/> operations.
    /// </para>
    /// </remarks>
    CancellationToken GetShutdownToken();
}
