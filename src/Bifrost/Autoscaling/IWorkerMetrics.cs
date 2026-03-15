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
    /// Gets the number of work items currently being processed by workers.
    /// </summary>
    /// <value>The count of in-flight work items.</value>
    long InFlightCount { get; }

    /// <summary>
    /// Records that a work item was enqueued.
    /// </summary>
    void RecordEnqueue();

    /// <summary>
    /// Records that a worker started processing a work item.
    /// </summary>
    void RecordExecutionStart();

    /// <summary>
    /// Records that a worker finished processing a work item.
    /// </summary>
    void RecordExecutionEnd();
}