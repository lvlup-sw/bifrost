// =============================================================================
// <copyright file="IWorkOrchestrator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;

namespace Bifrost.Core;

/// <summary>
/// Orchestrates work processing through a bounded channel with configurable workers.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <remarks>
/// <para>
/// The work orchestrator provides a high-performance, zero-allocation hot path for
/// enqueueing work items. It manages a pool of workers that process items from the
/// internal channel.
/// </para>
/// <para>
/// Key design principles:
/// <list type="bullet">
///   <item><description>ValueTask-based API for zero-allocation hot path</description></item>
///   <item><description>Non-allocating property access for observability</description></item>
///   <item><description>Escape hatch via Writer property for advanced scenarios</description></item>
/// </list>
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
    /// Gets the maximum capacity of the internal channel.
    /// </summary>
    /// <value>The channel capacity.</value>
    int Capacity { get; }

    /// <summary>
    /// Gets the underlying channel writer for advanced scenarios.
    /// </summary>
    /// <value>The channel writer for direct access.</value>
    /// <remarks>
    /// Use this escape hatch when you need direct access to the channel writer
    /// for specialized scenarios like batch operations or custom backpressure handling.
    /// </remarks>
    ChannelWriter<TWork> Writer { get; }

    /// <summary>
    /// Enqueues a work item for processing asynchronously.
    /// </summary>
    /// <param name="work">The work item to enqueue.</param>
    /// <param name="ct">Cancellation token to cancel the enqueue operation.</param>
    /// <returns>A ValueTask that completes when the item is enqueued.</returns>
    /// <remarks>
    /// This method may block asynchronously if the channel is at capacity,
    /// implementing backpressure behavior.
    /// </remarks>
    ValueTask EnqueueAsync(TWork work, CancellationToken ct = default);

    /// <summary>
    /// Attempts to enqueue a work item without blocking.
    /// </summary>
    /// <param name="work">The work item to enqueue.</param>
    /// <returns>True if the item was enqueued; false if the channel is full.</returns>
    /// <remarks>
    /// Use this method when you need non-blocking enqueue behavior and can
    /// handle rejection gracefully.
    /// </remarks>
    bool TryEnqueue(TWork work);

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
    /// <exception cref="InvalidOperationException">Thrown when the queue is full.</exception>
    /// <remarks>
    /// This method throws if the channel is at capacity. For non-throwing
    /// behavior, use <see cref="TryRun"/>.
    /// </remarks>
    void Run(TWork work);

    /// <summary>
    /// Attempts to enqueue a work item synchronously without throwing.
    /// </summary>
    /// <param name="work">The work item to enqueue.</param>
    /// <returns>True if the item was enqueued; false if the queue is full.</returns>
    /// <remarks>
    /// Use this method when you need non-throwing synchronous enqueue behavior
    /// and can handle rejection gracefully.
    /// </remarks>
    bool TryRun(TWork work);

    /// <summary>
    /// Creates a worker function that processes work from the internal channel.
    /// </summary>
    /// <returns>A function that can be used to start a worker loop.</returns>
    /// <remarks>
    /// The returned function takes a worker ID and cancellation token, and processes
    /// work items until the channel is completed or cancellation is requested.
    /// </remarks>
    Func<string, CancellationToken, Task> CreateWorkerFunction();

    /// <summary>
    /// Creates a worker function that processes work from the internal channel
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