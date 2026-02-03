// =============================================================================
// <copyright file="OrchestratorMetrics.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.Metrics;
using Bifrost.Core;

namespace Bifrost.OpenTelemetry;

/// <summary>
/// OpenTelemetry metrics for <see cref="IWorkOrchestrator{TWork}"/>.
/// </summary>
/// <typeparam name="TWork">The type of work items.</typeparam>
/// <remarks>
/// <para>
/// Provides the following metrics:
/// <list type="bullet">
///   <item><description><c>orchestrator.items.enqueued</c> - Counter for total items enqueued</description></item>
///   <item><description><c>orchestrator.items.processed</c> - Counter for total items processed</description></item>
///   <item><description><c>orchestrator.items.failed</c> - Counter for total items that failed processing</description></item>
///   <item><description><c>orchestrator.processing.duration</c> - Histogram for processing duration in milliseconds</description></item>
///   <item><description><c>orchestrator.queue.pending</c> - Observable gauge for pending items</description></item>
///   <item><description><c>orchestrator.workers.active</c> - Observable gauge for active workers</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class OrchestratorMetrics<TWork> : IDisposable
{
    private readonly Meter _meter;
    private bool _disposed;

    /// <summary>
    /// Gets the counter for total items enqueued.
    /// </summary>
    /// <value>Counter tracking enqueue operations.</value>
    public Counter<long> ItemsEnqueued { get; }

    /// <summary>
    /// Gets the counter for total items processed successfully.
    /// </summary>
    /// <value>Counter tracking successful processing operations.</value>
    public Counter<long> ItemsProcessed { get; }

    /// <summary>
    /// Gets the counter for total items that failed processing.
    /// </summary>
    /// <value>Counter tracking failed processing operations.</value>
    public Counter<long> ItemsFailed { get; }

    /// <summary>
    /// Gets the histogram for processing duration.
    /// </summary>
    /// <value>Histogram tracking processing duration in milliseconds.</value>
    public Histogram<double> ProcessingDuration { get; }

    /// <summary>
    /// Gets the observable gauge for pending items.
    /// </summary>
    /// <value>Observable gauge for current pending item count.</value>
    public ObservableGauge<int> PendingItems { get; }

    /// <summary>
    /// Gets the observable gauge for active workers.
    /// </summary>
    /// <value>Observable gauge for current active worker count.</value>
    public ObservableGauge<int> ActiveWorkers { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="OrchestratorMetrics{TWork}"/> class.
    /// </summary>
    /// <param name="orchestratorProvider">
    /// A function that provides the current orchestrator instance for observable gauges.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="orchestratorProvider"/> is null.
    /// </exception>
    public OrchestratorMetrics(Func<IWorkOrchestrator<TWork>?> orchestratorProvider)
    {
        ArgumentNullException.ThrowIfNull(orchestratorProvider);

        var workTypeName = typeof(TWork).Name;
        _meter = new Meter($"Bifrost.{workTypeName}", "1.0.0");

        ItemsEnqueued = _meter.CreateCounter<long>(
            "orchestrator.items.enqueued",
            unit: "{item}",
            description: "Total number of work items enqueued");

        ItemsProcessed = _meter.CreateCounter<long>(
            "orchestrator.items.processed",
            unit: "{item}",
            description: "Total number of work items processed successfully");

        ItemsFailed = _meter.CreateCounter<long>(
            "orchestrator.items.failed",
            unit: "{item}",
            description: "Total number of work items that failed processing");

        ProcessingDuration = _meter.CreateHistogram<double>(
            "orchestrator.processing.duration",
            unit: "ms",
            description: "Work item processing duration in milliseconds");

        PendingItems = _meter.CreateObservableGauge(
            "orchestrator.queue.pending",
            () => orchestratorProvider()?.PendingCount ?? 0,
            unit: "{item}",
            description: "Current number of work items pending in the queue");

        ActiveWorkers = _meter.CreateObservableGauge(
            "orchestrator.workers.active",
            () => orchestratorProvider()?.ActiveWorkers ?? 0,
            unit: "{worker}",
            description: "Current number of active workers processing items");
    }

    /// <summary>
    /// Records that a work item was enqueued.
    /// </summary>
    public void RecordEnqueued() => ItemsEnqueued.Add(1);

    /// <summary>
    /// Records that a work item was processed successfully.
    /// </summary>
    /// <param name="durationMs">The processing duration in milliseconds.</param>
    public void RecordProcessed(double durationMs)
    {
        ItemsProcessed.Add(1);
        ProcessingDuration.Record(durationMs);
    }

    /// <summary>
    /// Records that a work item failed processing.
    /// </summary>
    /// <param name="durationMs">The processing duration in milliseconds before failure.</param>
    public void RecordFailed(double durationMs)
    {
        ItemsFailed.Add(1);
        ProcessingDuration.Record(durationMs);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _meter.Dispose();
        _disposed = true;
    }
}
