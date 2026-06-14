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
///   <item><description><c>bifrost.orchestrator.queue_wait</c> - Histogram for queue wait in milliseconds, tagged by <c>work.class</c></description></item>
///   <item><description><c>bifrost.orchestrator.rejected</c> - Counter for items rejected at admission, tagged by <c>work.class</c> and <c>rejection.reason</c></description></item>
///   <item><description><c>orchestrator.items.deadlettered</c> - Counter for total items dead-lettered</description></item>
///   <item><description><c>orchestrator.queue.pending</c> - Observable gauge for pending items</description></item>
///   <item><description><c>orchestrator.workers.active</c> - Observable gauge for active workers</description></item>
///   <item><description><c>orchestrator.dlq.depth</c> - Observable gauge for dead letter queue depth (optional)</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class OrchestratorMetrics<TWork> : IDisposable
{
    // Cached work.class tag values: one per WorkClass member, so the per-dequeue
    // record path never calls Enum.ToString() and never allocates.
    private static readonly KeyValuePair<string, object?> InteractiveClassTag =
        new("work.class", nameof(WorkClass.Interactive));

    private static readonly KeyValuePair<string, object?> DefaultClassTag =
        new("work.class", nameof(WorkClass.Default));

    private static readonly KeyValuePair<string, object?> BatchClassTag =
        new("work.class", nameof(WorkClass.Batch));

    // Cached rejection.reason tag values: one per RejectionReason member, so the
    // per-rejection record path never calls Enum.ToString() and never allocates.
    private static readonly KeyValuePair<string, object?> CapacityExceededReasonTag =
        new("rejection.reason", nameof(RejectionReason.CapacityExceeded));

    private static readonly KeyValuePair<string, object?> WatermarkExceededReasonTag =
        new("rejection.reason", nameof(RejectionReason.WatermarkExceeded));

    private static readonly KeyValuePair<string, object?> ShutdownReasonTag =
        new("rejection.reason", nameof(RejectionReason.Shutdown));

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
    /// Gets the histogram for the time work items spend queued before dispatch,
    /// tagged by <c>work.class</c> (the <see cref="WorkClass"/> enum member name).
    /// </summary>
    /// <value>Histogram tracking queue wait in milliseconds, tagged by work class.</value>
    /// <remarks>
    /// <para>
    /// This instrument is the Stage-2 evidence signal for priority dispatch
    /// (issue #17). Evidence recipe: when the <c>Interactive</c>-class p95 queue wait
    /// exceeds the 500 ms operator budget while <c>Batch</c>-class work is
    /// co-resident, consider enabling priority dispatch.
    /// </para>
    /// <para>
    /// Recorded values are computed exclusively from <see cref="TimeProvider"/>
    /// monotonic elapsed time (<see cref="TimeProvider.GetElapsedTime(long)"/> over
    /// the envelope's enqueue timestamp). FireTime-style wall-clock timestamps are
    /// never used, so the measurement is immune to clock adjustments.
    /// </para>
    /// </remarks>
    public Histogram<double> QueueWait { get; }

    /// <summary>
    /// Gets the counter for work items rejected at admission (DR-6), tagged by
    /// <c>work.class</c> and <c>rejection.reason</c>.
    /// </summary>
    /// <value>Counter tracking admission rejections, tagged by class and reason.</value>
    /// <remarks>
    /// <para>
    /// Rejections are always counted, independent of dead-letter configuration;
    /// dead-letter routing of rejected work additionally requires
    /// <c>WithDeadLetterQueue()</c>. <see cref="RejectionReason.Shutdown"/>
    /// rejections during teardown are counted (tagged by reason) but never
    /// dead-lettered.
    /// </para>
    /// <para>
    /// This counter is the rejection-side companion of the
    /// <see cref="QueueWait"/> histogram in the Stage-2 evidence recipe for
    /// priority dispatch (issue #17): sustained non-shutdown rejections alongside
    /// an elevated interactive-class queue-wait p95 indicate admission pressure
    /// rather than transient bursts.
    /// </para>
    /// </remarks>
    public Counter<long> Rejected { get; }

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
    /// Gets the counter for total items dead-lettered.
    /// </summary>
    /// <value>Counter tracking dead-lettered items.</value>
    public Counter<long> ItemsDeadLettered { get; }

    /// <summary>
    /// Gets the observable gauge for dead letter queue depth.
    /// </summary>
    /// <value>Observable gauge for current DLQ item count, or <c>null</c> if no DLQ provider was configured.</value>
    public ObservableGauge<int>? DeadLetterQueueDepth { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="OrchestratorMetrics{TWork}"/> class.
    /// </summary>
    /// <param name="orchestratorProvider">
    /// A function that provides the current orchestrator instance for observable gauges.
    /// </param>
    /// <param name="dlqCountProvider">
    /// Optional function that provides the current DLQ item count for the observable gauge.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="orchestratorProvider"/> is null.
    /// </exception>
    public OrchestratorMetrics(
        Func<IWorkOrchestrator<TWork>?> orchestratorProvider,
        Func<int>? dlqCountProvider = null)
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

        ItemsDeadLettered = _meter.CreateCounter<long>(
            "orchestrator.items.deadlettered",
            unit: "{item}",
            description: "Total number of work items dead-lettered");

        ProcessingDuration = _meter.CreateHistogram<double>(
            "orchestrator.processing.duration",
            unit: "ms",
            description: "Work item processing duration in milliseconds");

        QueueWait = _meter.CreateHistogram<double>(
            "bifrost.orchestrator.queue_wait",
            unit: "ms",
            description: "Time work items spend queued before dispatch, tagged by work.class. " +
                "Stage-2 evidence for priority dispatch (issue #17): interactive-class p95 " +
                "queue wait > 500 ms while batch-class work is co-resident.");

        Rejected = _meter.CreateCounter<long>(
            "bifrost.orchestrator.rejected",
            unit: "{item}",
            description: "Total number of work items rejected at admission, tagged by " +
                "work.class and rejection.reason (DR-6). Rejection-side companion of " +
                "bifrost.orchestrator.queue_wait in the Stage-2 priority-dispatch evidence recipe.");

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

        if (dlqCountProvider is not null)
        {
            DeadLetterQueueDepth = _meter.CreateObservableGauge(
                "orchestrator.dlq.depth",
                dlqCountProvider,
                unit: "{item}",
                description: "Current number of items in the dead letter queue");
        }
    }

    /// <summary>
    /// Records that a work item was enqueued.
    /// </summary>
    public void RecordEnqueued() => ItemsEnqueued.Add(1);

    /// <summary>
    /// Records that a work item was dead-lettered.
    /// </summary>
    public void RecordDeadLettered() => ItemsDeadLettered.Add(1);

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
    /// Records the time a work item spent queued before dispatch.
    /// </summary>
    /// <param name="workClass">The work class of the dequeued item.</param>
    /// <param name="waited">
    /// The queue wait computed from <see cref="TimeProvider"/> monotonic elapsed time.
    /// </param>
    /// <remarks>
    /// Allocation-free per record: the <c>work.class</c> tag values are cached per
    /// <see cref="WorkClass"/> member, so no string or boxing allocation occurs on
    /// the dequeue hot path.
    /// </remarks>
    public void RecordQueueWait(WorkClass workClass, TimeSpan waited)
        => QueueWait.Record(waited.TotalMilliseconds, workClass switch
        {
            WorkClass.Interactive => InteractiveClassTag,
            WorkClass.Batch => BatchClassTag,
            _ => DefaultClassTag,
        });

    /// <summary>
    /// Records that a work item was rejected at admission (DR-6).
    /// </summary>
    /// <param name="workClass">The work class of the rejected item.</param>
    /// <param name="reason">The admission rejection reason.</param>
    /// <remarks>
    /// Allocation-free per record: the <c>work.class</c> and
    /// <c>rejection.reason</c> tag values are cached per enum member, so no
    /// string or boxing allocation occurs on the rejection path.
    /// </remarks>
    public void RecordRejected(WorkClass workClass, RejectionReason reason)
        => Rejected.Add(
            1,
            workClass switch
            {
                WorkClass.Interactive => InteractiveClassTag,
                WorkClass.Batch => BatchClassTag,
                _ => DefaultClassTag,
            },
            reason switch
            {
                RejectionReason.WatermarkExceeded => WatermarkExceededReasonTag,
                RejectionReason.Shutdown => ShutdownReasonTag,
                _ => CapacityExceededReasonTag,
            });

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