// =============================================================================
// <copyright file="WorkerMetrics.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Autoscaling;

/// <summary>
/// Thread-safe implementation of <see cref="IWorkerMetrics"/> for tracking work orchestrator utilization.
/// </summary>
/// <remarks>
/// <para>
/// This implementation uses Interlocked operations for thread-safety,
/// allowing accurate metrics tracking under high concurrency.
/// </para>
/// <para>
/// The utilization ratio is calculated as pending work divided by max backlog,
/// capped at 1.0 to indicate full or over capacity.
/// </para>
/// </remarks>
public sealed class WorkerMetrics : IWorkerMetrics
{
    private long _inFlightCount;

    /// <inheritdoc/>
    public long InFlightCount => Interlocked.Read(ref _inFlightCount);

    /// <inheritdoc/>
    /// <remarks>
    /// This method does nothing. Enqueue tracking is only a signal for the autoscaling decorator.
    /// <see cref="AutoscalingCoordinator{TWork}.GetUtilizationRatio"/> computes the utilization, and it reads
    /// the actual <c>PendingCount</c> directly.
    /// </remarks>
    public void RecordEnqueue()
    {
    }

    /// <inheritdoc/>
    public void RecordExecutionStart()
    {
        Interlocked.Increment(ref _inFlightCount);
    }

    /// <inheritdoc/>
    public void RecordExecutionEnd()
    {
        Interlocked.Decrement(ref _inFlightCount);
    }
}