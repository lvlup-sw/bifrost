// =============================================================================
// <copyright file="IAutoscalingMetricsPort.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Autoscaling.Ports;

/// <summary>
/// Provides metrics from the orchestrator for autoscaling decisions.
/// </summary>
/// <remarks>
/// <para>
/// This port interface provides read-only access to orchestrator metrics
/// that the autoscaling engine needs to make scaling decisions. The metrics
/// include queue depth, worker counts, and utilization ratios.
/// </para>
/// <para>
/// The port pattern enables the autoscaling components to depend on an
/// abstraction rather than the concrete orchestrator, resolving circular
/// dependency issues in the architecture.
/// </para>
/// </remarks>
public interface IAutoscalingMetricsPort
{
    /// <summary>
    /// Gets the maximum number of work items that can be queued.
    /// </summary>
    /// <value>The maximum backlog capacity.</value>
    int MaxBacklog { get; }

    /// <summary>
    /// Gets the number of work items currently pending in the queue.
    /// </summary>
    /// <value>The count of pending work items.</value>
    int PendingWorkCount { get; }

    /// <summary>
    /// Gets the number of currently active workers processing items.
    /// </summary>
    /// <value>The count of active workers.</value>
    int ActiveWorkerCount { get; }

    /// <summary>
    /// Gets the total number of events queued for publishing.
    /// </summary>
    /// <value>The count of queued events.</value>
    long QueuedCount { get; }

    /// <summary>
    /// Calculates the current utilization ratio.
    /// </summary>
    /// <returns>
    /// A value between 0.0 and 1.0 (or higher if over capacity) representing
    /// the ratio of pending work to capacity. Returns 0.0 if capacity is zero.
    /// </returns>
    /// <remarks>
    /// The utilization ratio is calculated as: PendingWorkCount / MaxBacklog.
    /// This method includes protection against division by zero.
    /// </remarks>
    double GetUtilizationRatio();
}