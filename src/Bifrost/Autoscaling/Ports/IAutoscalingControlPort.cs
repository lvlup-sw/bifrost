// =============================================================================
// <copyright file="IAutoscalingControlPort.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Autoscaling.Ports;

/// <summary>
/// Provides control operations for autoscaling workers.
/// </summary>
/// <remarks>
/// <para>
/// This port interface provides methods for scaling workers up and down,
/// creating worker functions, and obtaining shutdown tokens. These operations
/// are called by the autoscaling engine to implement scaling decisions.
/// </para>
/// <para>
/// The port pattern enables the autoscaling components to depend on an
/// abstraction rather than the concrete orchestrator, resolving circular
/// dependency issues in the architecture.
/// </para>
/// </remarks>
public interface IAutoscalingControlPort
{
    /// <summary>
    /// Requests scaling up the worker pool by the specified count.
    /// </summary>
    /// <param name="count">The number of workers to add.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>A task that completes when the scale-up request has been processed.</returns>
    /// <remarks>
    /// This method requests additional workers to be spawned. The actual number
    /// of workers added may be constrained by configuration limits (e.g., MaxWorkers).
    /// </remarks>
    Task RequestScaleUpAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests scaling down the worker pool by the specified count.
    /// </summary>
    /// <param name="count">The number of workers to remove.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>A task that completes when the scale-down request has been processed.</returns>
    /// <remarks>
    /// This method requests workers to be gracefully terminated. The actual number
    /// of workers removed may be constrained by configuration limits (e.g., MinWorkers).
    /// </remarks>
    Task RequestScaleDownAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a worker function that processes work items from the orchestrator.
    /// </summary>
    /// <returns>A function that accepts a worker ID and cancellation token.</returns>
    /// <remarks>
    /// The returned function can be used to spawn new worker tasks. Each worker
    /// will process work items from the internal channel until cancelled.
    /// </remarks>
    Func<string, CancellationToken, Task> CreateWorkerFunction();

    /// <summary>
    /// Creates a worker function with a state callback for tracking worker activity.
    /// </summary>
    /// <param name="stateCallback">
    /// A callback invoked with true when work processing starts
    /// and false when it completes or is cancelled.
    /// </param>
    /// <returns>A function that accepts a worker ID and cancellation token.</returns>
    /// <remarks>
    /// The state callback enables external tracking of worker activity without
    /// coupling the orchestrator to specific monitoring implementations.
    /// </remarks>
    Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback);

    /// <summary>
    /// Gets the shutdown token for coordinated orchestrator shutdown.
    /// </summary>
    /// <returns>A cancellation token that is cancelled when the orchestrator shuts down.</returns>
    /// <remarks>
    /// Workers should observe this token to gracefully stop processing
    /// when the orchestrator is shutting down.
    /// </remarks>
    CancellationToken GetShutdownToken();
}
