// =============================================================================
// <copyright file="IWorkOrchestrator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>Owns workers and queue lifecycle. Graceful completion attempts admitted work in memory;
/// abort, handler failures, and process failure may prevent successful execution.</summary>
/// <typeparam name="TWork">The work item type.</typeparam>
public interface IWorkOrchestrator<TWork> : IWorkWriter<TWork>, IAsyncDisposable
{
    /// <summary>Gets the observed pending count.</summary>
    int PendingCount { get; }

    /// <summary>Gets the active worker count.</summary>
    int ActiveWorkers { get; }

    /// <summary>Gets the queue capacity.</summary>
    int Capacity { get; }

    /// <summary>Closes admission. Already admitted items remain available to workers.</summary>
    void Complete();

    /// <summary>Gets a task completing after admission closes and all registered workers exit.</summary>
    Task Completion { get; }

    /// <summary>Closes admission and waits for all workers. Canceling the wait does not abort workers.</summary>
    /// <param name="ct">Cancels only the wait.</param>
    /// <returns>A task representing the drain.</returns>
    Task DrainAsync(CancellationToken ct = default);

    /// <summary>Closes admission and signals worker cancellation. Pending work may be abandoned.</summary>
    /// <param name="ct">Cancels the wait for workers to exit.</param>
    /// <returns>A task representing worker termination.</returns>
    Task AbortAsync(CancellationToken ct = default);

    /// <summary>Gets the token canceled on abort or disposal.</summary>
    /// <returns>The worker shutdown token.</returns>
    CancellationToken GetShutdownToken();

    /// <summary>Creates a worker loop that registers on invocation and exits on completion or cancellation.</summary>
    /// <returns>The worker loop.</returns>
    Func<string, CancellationToken, Task> CreateWorkerFunction();

    /// <summary>Creates a worker loop with state tracking.</summary>
    /// <param name="stateCallback">Called with true while busy and false after handling.</param>
    /// <returns>The worker loop.</returns>
    Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback);

    /// <summary>Requests additional workers when autoscaling is enabled.</summary>
    /// <param name="count">Number to add.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task representing the request.</returns>
    Task RequestScaleUpAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>Requests fewer workers when autoscaling is enabled.</summary>
    /// <param name="count">Number to stop.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task representing the request.</returns>
    Task RequestScaleDownAsync(int count, CancellationToken cancellationToken = default);
}
