// =============================================================================
// <copyright file="OrchestratorMetrics.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.Metrics;

using Bifrost.Core;
using Bifrost.Outcomes;

namespace Bifrost.OpenTelemetry;

/// <summary>
/// OpenTelemetry metrics for <see cref="IWorkOrchestrator{TWork}"/>.
/// </summary>
/// <typeparam name="TWork">The type of work items.</typeparam>
/// <remarks>
/// <para>
/// Provides the following metrics:
/// <list type="bullet">
///   <item><description><c>orchestrator.items.enqueued</c> - Counter for total items enqueued (manual)</description></item>
///   <item><description><c>orchestrator.items.processed</c> - Counter for items whose last handler attempt succeeded</description></item>
///   <item><description><c>orchestrator.items.failed</c> - Counter for items whose last handler attempt failed</description></item>
///   <item><description><c>orchestrator.processing.duration</c> - Histogram for processing duration in milliseconds, tagged by <c>outcome</c></description></item>
///   <item><description><c>bifrost.orchestrator.queue_wait</c> - Histogram for queue wait in milliseconds, tagged by <c>work.class</c></description></item>
///   <item><description><c>bifrost.orchestrator.rejected</c> - Counter for items rejected at admission, tagged by <c>work.class</c> and <c>rejection.reason</c></description></item>
///   <item><description><c>bifrost.orchestrator.outcomes</c> - Observable counter for terminal outcomes, tagged by <c>outcome</c></description></item>
///   <item><description><c>bifrost.orchestrator.handler.attempts</c> - Observable counter for handler attempts, including dead-letter retries</description></item>
///   <item><description><c>bifrost.orchestrator.deadletter.captured</c> - Observable counter for final handler failures stored in the dead-letter queue</description></item>
///   <item><description><c>bifrost.orchestrator.deadletter.capture_errors</c> - Observable counter for final handler failures the dead-letter queue failed to store</description></item>
///   <item><description><c>bifrost.orchestrator.deadletter.evictions</c> - Observable counter for entries the built-in dead-letter queue dropped because it was full</description></item>
///   <item><description><c>bifrost.orchestrator.deadletter.notifications_dropped</c> - Observable counter for dead-letter subscriber notifications that were dropped</description></item>
///   <item><description><c>orchestrator.items.deadlettered</c> - Counter for total items dead-lettered (manual)</description></item>
///   <item><description><c>orchestrator.queue.pending</c> - Observable gauge for pending items</description></item>
///   <item><description><c>bifrost.orchestrator.queue.ready</c> - Observable gauge for pending items a free worker could take now</description></item>
///   <item><description><c>bifrost.orchestrator.queue.blocked</c> - Observable gauge for pending items that wait for a key slot</description></item>
///   <item><description><c>orchestrator.workers.active</c> - Observable gauge for active workers</description></item>
///   <item><description><c>orchestrator.dlq.depth</c> - Observable gauge for dead letter queue depth (optional)</description></item>
/// </list>
/// </para>
/// <para>
/// <c>WithOpenTelemetry</c> attaches the instance to the base orchestrator when it is built. From
/// then on, the processed and failed counters, the processing-duration histogram, the outcome,
/// attempt, and dead-letter counters, and the ready and blocked gauges are recorded automatically
/// from the orchestrator's terminal outcomes and queue backlog. An instance created with the
/// public constructor and never attached reports no outcome, attempt, or dead-letter counts, reports
/// every pending item as ready, and reports no blocked items.
/// </para>
/// <para>
/// Every tag value comes from a fixed set (<c>work.class</c>, <c>rejection.reason</c>, and
/// <c>outcome</c> enum member names). Work items and work keys never appear as tags, so the number
/// of series stays fixed under keyed dispatch with any number of keys.
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

    private static readonly KeyValuePair<string, object?> KeyLimitExceededReasonTag =
        new("rejection.reason", nameof(RejectionReason.KeyLimitExceeded));

    // Cached outcome tag values: one per TerminalOutcome member, so the per-item
    // record path never calls Enum.ToString() and never allocates.
    private static readonly KeyValuePair<string, object?> SucceededOutcomeTag =
        new("outcome", nameof(TerminalOutcome.Succeeded));

    private static readonly KeyValuePair<string, object?> FailedOutcomeTag =
        new("outcome", nameof(TerminalOutcome.Failed));

    private static readonly KeyValuePair<string, object?> CanceledOutcomeTag =
        new("outcome", nameof(TerminalOutcome.Canceled));

    private static readonly KeyValuePair<string, object?> AbandonedOutcomeTag =
        new("outcome", nameof(TerminalOutcome.Abandoned));

    private readonly Func<IWorkOrchestrator<TWork>?> _orchestratorProvider;
    private readonly Meter _meter;
    private WorkOrchestrator<TWork>? _attached;
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
    /// <remarks>
    /// When the instance is attached by <c>WithOpenTelemetry</c>, one value is recorded per started
    /// item, after its last handler attempt, tagged by <c>outcome</c> (<c>Succeeded</c>,
    /// <c>Failed</c>, or <c>Canceled</c>). The value covers all attempts of the item, including
    /// dead-letter retries. Abandoned items never started and record no duration.
    /// </remarks>
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
    /// rejections during teardown and <see cref="RejectionReason.KeyLimitExceeded"/>
    /// rejections from keyed dispatch are counted (tagged by reason) but never
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
    /// Gets the observable gauge for pending items that a free worker could take now.
    /// </summary>
    /// <value>Observable gauge for the ready part of the pending count.</value>
    /// <remarks>
    /// With keyed dispatch this is the dispatchable backlog: queued items whose key is below its
    /// concurrency limit. Without keyed dispatch every pending item is ready. The value is an
    /// aggregate count and carries no work key.
    /// </remarks>
    public ObservableGauge<int> ReadyItems { get; }

    /// <summary>
    /// Gets the observable gauge for pending items that wait for a key slot.
    /// </summary>
    /// <value>Observable gauge for the blocked part of the pending count.</value>
    /// <remarks>
    /// With keyed dispatch these are queued items behind a key at its concurrency limit. They run
    /// only after a running item of that key releases its slot, so more workers do not help them.
    /// Without keyed dispatch the value is always zero. The value is an aggregate count and carries
    /// no work key.
    /// </remarks>
    public ObservableGauge<int> BlockedItems { get; }

    /// <summary>
    /// Gets the observable counter for terminal outcomes, tagged by <c>outcome</c>.
    /// </summary>
    /// <value>
    /// Observable counter with one series per outcome: <c>Succeeded</c>, <c>Failed</c>,
    /// <c>Canceled</c>, and <c>Abandoned</c>.
    /// </value>
    /// <remarks>
    /// Every accepted item ends with exactly one outcome. An item that fails and then succeeds on a
    /// dead-letter retry counts once, as <c>Succeeded</c>. <c>Canceled</c> means the handler threw
    /// <see cref="OperationCanceledException"/> after its token was cancelled, for example by
    /// <c>AbortAsync</c>. <c>Abandoned</c> means the item was accepted but removed from the queue by
    /// abort or completion before any worker started it.
    /// </remarks>
    public ObservableCounter<long> Outcomes { get; }

    /// <summary>
    /// Gets the observable counter for handler attempts, including dead-letter retries.
    /// </summary>
    /// <value>Observable counter for the total number of handler calls.</value>
    /// <remarks>
    /// Attempts are counted when the item settles, after its last attempt. The attempts of an item
    /// that is still running do not show until it finishes.
    /// </remarks>
    public ObservableCounter<long> HandlerAttempts { get; }

    /// <summary>
    /// Gets the observable counter for final handler failures stored in the dead-letter queue.
    /// </summary>
    /// <value>Observable counter for successful dead-letter captures.</value>
    /// <remarks>
    /// Each final handler failure is captured at most once. Admission rejections that the
    /// rejection-routing decorator dead-letters are not counted here.
    /// </remarks>
    public ObservableCounter<long> DeadLetterCaptured { get; }

    /// <summary>
    /// Gets the observable counter for final handler failures the dead-letter queue failed to store.
    /// </summary>
    /// <value>Observable counter for dead-letter capture errors.</value>
    public ObservableCounter<long> DeadLetterCaptureErrors { get; }

    /// <summary>
    /// Gets the observable counter for entries the built-in dead-letter queue dropped because it was full.
    /// </summary>
    /// <value>Observable counter for dead-letter evictions.</value>
    public ObservableCounter<long> DeadLetterEvictions { get; }

    /// <summary>
    /// Gets the observable counter for dead-letter subscriber notifications that were dropped.
    /// </summary>
    /// <value>Observable counter for dropped dead-letter notifications.</value>
    public ObservableCounter<long> DeadLetterNotificationsDropped { get; }

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
        _orchestratorProvider = orchestratorProvider;

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

        ReadyItems = _meter.CreateObservableGauge(
            "bifrost.orchestrator.queue.ready",
            ObserveReady,
            unit: "{item}",
            description: "Current number of pending work items that a free worker could take now");

        BlockedItems = _meter.CreateObservableGauge(
            "bifrost.orchestrator.queue.blocked",
            ObserveBlocked,
            unit: "{item}",
            description: "Current number of pending work items that wait for a key slot");

        Outcomes = _meter.CreateObservableCounter(
            "bifrost.orchestrator.outcomes",
            ObserveOutcomes,
            unit: "{item}",
            description: "Total number of accepted work items by terminal outcome, tagged by outcome");

        HandlerAttempts = _meter.CreateObservableCounter(
            "bifrost.orchestrator.handler.attempts",
            () => ObserveCounter(static c => c.HandlerAttempts),
            unit: "{attempt}",
            description: "Total number of handler attempts, including dead-letter retries");

        DeadLetterCaptured = _meter.CreateObservableCounter(
            "bifrost.orchestrator.deadletter.captured",
            () => ObserveCounter(static c => c.DeadLetterCaptured),
            unit: "{item}",
            description: "Total number of final handler failures stored in the dead letter queue");

        DeadLetterCaptureErrors = _meter.CreateObservableCounter(
            "bifrost.orchestrator.deadletter.capture_errors",
            () => ObserveCounter(static c => c.DeadLetterCaptureErrors),
            unit: "{item}",
            description: "Total number of final handler failures the dead letter queue failed to store");

        DeadLetterEvictions = _meter.CreateObservableCounter(
            "bifrost.orchestrator.deadletter.evictions",
            () => ObserveCounter(static c => c.DeadLetterEvictions),
            unit: "{item}",
            description: "Total number of entries the built-in dead letter queue dropped because it was full");

        DeadLetterNotificationsDropped = _meter.CreateObservableCounter(
            "bifrost.orchestrator.deadletter.notifications_dropped",
            () => ObserveCounter(static c => c.DeadLetterNotificationsDropped),
            unit: "{notification}",
            description: "Total number of dead letter subscriber notifications that were dropped");

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
    /// <remarks>
    /// <c>WithOpenTelemetry</c> records processed items and their duration automatically from the
    /// orchestrator's terminal outcomes. Calling this method as well counts each item twice. Remove
    /// the manual call from the handler. See <c>docs/migrations/2026-11-automatic-metrics.md</c>.
    /// </remarks>
    [Obsolete("WithOpenTelemetry records processed items and their duration automatically. Remove the manual call to avoid counting each item twice.")]
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
                RejectionReason.CapacityExceeded => CapacityExceededReasonTag,
                RejectionReason.WatermarkExceeded => WatermarkExceededReasonTag,
                RejectionReason.Shutdown => ShutdownReasonTag,
                RejectionReason.KeyLimitExceeded => KeyLimitExceededReasonTag,
                _ => CapacityExceededReasonTag,
            });

    /// <summary>
    /// Records that a work item failed processing.
    /// </summary>
    /// <param name="durationMs">The processing duration in milliseconds before failure.</param>
    /// <remarks>
    /// <c>WithOpenTelemetry</c> records failed items and their duration automatically from the
    /// orchestrator's terminal outcomes, once per item after its last attempt. Calling this method
    /// as well counts each failure twice, and a manual call in the handler counts every dead-letter
    /// retry as a failed item. Remove the manual call from the handler. See
    /// <c>docs/migrations/2026-11-automatic-metrics.md</c>.
    /// </remarks>
    [Obsolete("WithOpenTelemetry records failed items and their duration automatically. Remove the manual call to avoid counting each failure twice.")]
    public void RecordFailed(double durationMs)
    {
        ItemsFailed.Add(1);
        ProcessingDuration.Record(durationMs);
    }

    /// <summary>
    /// Attaches the base orchestrator, so its terminal outcomes and backlog feed the instruments.
    /// </summary>
    /// <param name="orchestrator">The undecorated orchestrator.</param>
    /// <remarks>
    /// <para>
    /// <c>WithOpenTelemetry</c> calls this once at build time from a pass-through decorator at the
    /// innermost order, where the bare orchestrator is still visible. It binds
    /// <see cref="RecordQueueWait"/> to the queue-wait hook and <see cref="RecordOutcome"/> to the
    /// terminal outcome stage as method-group delegates, so the per-item record paths stay
    /// closure-free and allocation-free.
    /// </para>
    /// <para>
    /// The outcome, attempt, and dead-letter counters read the stage's shared aggregate counters
    /// when a collector asks. The pending, ready, and blocked gauges each sample the queue on their
    /// own, so within one collection pending can differ from ready plus blocked while items move;
    /// in a quiet queue they agree. None of them reads a work item or a work key.
    /// </para>
    /// <para>
    /// The outcome hook is added with <c>+=</c>, so another observer of the terminal outcome stage
    /// is kept, not replaced.
    /// </para>
    /// </remarks>
    internal void Attach(WorkOrchestrator<TWork> orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        orchestrator.QueueWaitObserved = RecordQueueWait;
        orchestrator.Outcomes.Observed += RecordOutcome;
        Volatile.Write(ref _attached, orchestrator);
    }

    /// <summary>
    /// Records the processing metrics of one settled item.
    /// </summary>
    /// <param name="record">The item's terminal outcome record.</param>
    /// <remarks>
    /// Called by the terminal outcome stage once per settled item, after the handler pipeline and
    /// its scope have exited and the key slot is released. <c>Succeeded</c> adds to
    /// <see cref="ItemsProcessed"/>, <c>Failed</c> adds to <see cref="ItemsFailed"/>, and every
    /// started item records its duration across all attempts in <see cref="ProcessingDuration"/>
    /// tagged by outcome. A <c>Canceled</c> item records only its duration. An <c>Abandoned</c> item
    /// never started and records nothing here; it is counted by <see cref="Outcomes"/>.
    /// Allocation-free: the outcome tags are cached.
    /// </remarks>
    internal void RecordOutcome(TerminalOutcomeRecord record)
    {
        var durationMs = record.Duration.TotalMilliseconds;
        switch (record.Outcome)
        {
            case TerminalOutcome.Succeeded:
                ItemsProcessed.Add(1);
                ProcessingDuration.Record(durationMs, SucceededOutcomeTag);
                break;
            case TerminalOutcome.Failed:
                ItemsFailed.Add(1);
                ProcessingDuration.Record(durationMs, FailedOutcomeTag);
                break;
            case TerminalOutcome.Canceled:
                ProcessingDuration.Record(durationMs, CanceledOutcomeTag);
                break;
            default:
                break;
        }
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

    private int ObserveReady()
    {
        if (Volatile.Read(ref _attached) is { } attached)
        {
            return attached.Backlog.Ready;
        }

        return _orchestratorProvider()?.PendingCount ?? 0;
    }

    private int ObserveBlocked() => Volatile.Read(ref _attached)?.Backlog.Blocked ?? 0;

    private IEnumerable<Measurement<long>> ObserveOutcomes()
    {
        if (Volatile.Read(ref _attached)?.Outcomes.Counters is not { } counters)
        {
            return [];
        }

        return
        [
            new Measurement<long>(counters.Succeeded, SucceededOutcomeTag),
            new Measurement<long>(counters.Failed, FailedOutcomeTag),
            new Measurement<long>(counters.Canceled, CanceledOutcomeTag),
            new Measurement<long>(counters.Abandoned, AbandonedOutcomeTag),
        ];
    }

    private IEnumerable<Measurement<long>> ObserveCounter(Func<TerminalOutcomeCounters<TWork>, long> read)
    {
        if (Volatile.Read(ref _attached)?.Outcomes.Counters is not { } counters)
        {
            return [];
        }

        return [new Measurement<long>(read(counters))];
    }
}
