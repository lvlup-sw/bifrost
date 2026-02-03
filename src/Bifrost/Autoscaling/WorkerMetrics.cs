// =============================================================================
// <copyright file="WorkerMetrics.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Levelup.Channels.Autoscaling;

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
    private long _pendingCount;
    private long _inFlightCount;

    /// <inheritdoc/>
    public long PendingWorkCount => Interlocked.Read(ref _pendingCount);

    /// <inheritdoc/>
    public long InFlightCount => Interlocked.Read(ref _inFlightCount);

    /// <inheritdoc/>
    public double CalculateUtilizationRatio(int maxBacklog)
    {
        if (maxBacklog <= 0)
        {
            return 1.0;
        }

        var pending = Interlocked.Read(ref _pendingCount);
        var ratio = (double)pending / maxBacklog;
        return Math.Min(ratio, 1.0);
    }

    /// <inheritdoc/>
    public void RecordEnqueue()
    {
        Interlocked.Increment(ref _pendingCount);
    }

    /// <inheritdoc/>
    public void RecordDequeue()
    {
        Interlocked.Decrement(ref _pendingCount);
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
