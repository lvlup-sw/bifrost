// =============================================================================
// <copyright file="IWorkerRegistry.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Levelup.Channels.Autoscaling;

/// <summary>
/// Manages the dynamic registration and lifecycle of worker instances.
/// </summary>
/// <remarks>
/// <para>
/// The worker registry provides thread-safe management of workers,
/// supporting dynamic scaling operations by the autoscaling engine.
/// </para>
/// <para>
/// Workers are identified by unique string IDs and can be individually
/// or batch-stopped based on scaling decisions.
/// </para>
/// </remarks>
public interface IWorkerRegistry
{
    /// <summary>
    /// Gets the total number of active workers.
    /// </summary>
    /// <value>The count of active workers.</value>
    int ActiveWorkerCount { get; }

    /// <summary>
    /// Gets the number of workers currently idle.
    /// </summary>
    /// <value>The count of idle workers.</value>
    int IdleWorkerCount { get; }

    /// <summary>
    /// Gets all registered workers.
    /// </summary>
    /// <returns>A read-only collection of all worker information.</returns>
    IReadOnlyCollection<WorkerInfo> GetAllWorkers();

    /// <summary>
    /// Gets information for a specific worker.
    /// </summary>
    /// <param name="workerId">The identifier of the worker to retrieve.</param>
    /// <returns>The worker information, or null if not found.</returns>
    WorkerInfo? GetWorkerInfo(string workerId);

    /// <summary>
    /// Creates and registers a new worker.
    /// </summary>
    /// <param name="workerId">The unique identifier for the worker.</param>
    /// <param name="workerFunction">The function the worker will execute.</param>
    /// <param name="ct">Cancellation token for the worker.</param>
    /// <returns>Information about the created worker.</returns>
    /// <exception cref="ArgumentNullException">Thrown when workerId or workerFunction is null.</exception>
    Task<WorkerInfo> CreateWorkerAsync(
        string workerId,
        Func<string, CancellationToken, Task> workerFunction,
        CancellationToken ct);

    /// <summary>
    /// Requests a specific worker to stop gracefully.
    /// </summary>
    /// <param name="workerId">The identifier of the worker to stop.</param>
    /// <returns>True if the worker was found and stop was requested; false otherwise.</returns>
    /// <remarks>
    /// This sets a stop flag on the worker. The worker should check this flag
    /// and exit after completing its current work item.
    /// </remarks>
    bool RequestWorkerStop(string workerId);

    /// <summary>
    /// Requests multiple workers to stop gracefully.
    /// </summary>
    /// <param name="count">The number of workers to stop.</param>
    /// <returns>The actual number of workers that were requested to stop.</returns>
    /// <remarks>
    /// <para>
    /// Idle workers are stopped first, followed by busy workers if needed.
    /// Workers are selected in LIFO order (newest first).
    /// </para>
    /// <para>
    /// The returned count may be less than requested if fewer workers are available.
    /// </para>
    /// </remarks>
    int RequestMultipleWorkerStop(int count);

    /// <summary>
    /// Gets a snapshot of the current worker registry state.
    /// </summary>
    /// <returns>An immutable snapshot of the registry state.</returns>
    /// <remarks>
    /// <para>
    /// The snapshot provides a consistent view of all worker counts at a single point in time.
    /// This is useful for diagnostics and monitoring where individual property reads might
    /// observe different states due to concurrent modifications.
    /// </para>
    /// </remarks>
    WorkerRegistrySnapshot GetSnapshot();
}
