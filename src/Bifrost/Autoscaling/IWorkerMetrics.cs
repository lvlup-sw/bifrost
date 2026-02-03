// =============================================================================
// <copyright file="IWorkerMetrics.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Autoscaling;

/// <summary>
/// Provides metrics for tracking work orchestrator utilization.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be thread-safe as metrics are accessed from
/// multiple workers concurrently.
/// </para>
/// <para>
/// The metrics are used by the autoscaling engine to make scaling decisions
/// based on current queue depth and worker utilization.
/// </para>
/// </remarks>
public interface IWorkerMetrics
{
    /// <summary>
    /// Gets the number of work items currently pending in the queue.
    /// </summary>
    /// <value>The count of pending work items.</value>
    long PendingWorkCount { get; }

    /// <summary>
    /// Gets the number of work items currently being processed by workers.
    /// </summary>
    /// <value>The count of in-flight work items.</value>
    long InFlightCount { get; }

    /// <summary>
    /// Calculates the utilization ratio based on pending work.
    /// </summary>
    /// <param name="maxBacklog">The maximum backlog capacity for ratio calculation.</param>
    /// <returns>
    /// A value between 0.0 and 1.0 representing utilization, where 1.0 indicates
    /// the queue is at or above maximum capacity.
    /// </returns>
    double CalculateUtilizationRatio(int maxBacklog);

    /// <summary>
    /// Records that a work item was enqueued.
    /// </summary>
    void RecordEnqueue();

    /// <summary>
    /// Records that a work item was dequeued from the pending queue.
    /// </summary>
    void RecordDequeue();

    /// <summary>
    /// Records that a worker started processing a work item.
    /// </summary>
    void RecordExecutionStart();

    /// <summary>
    /// Records that a worker finished processing a work item.
    /// </summary>
    void RecordExecutionEnd();
}
