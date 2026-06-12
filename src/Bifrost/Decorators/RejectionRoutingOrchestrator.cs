// =============================================================================
// <copyright file="RejectionRoutingOrchestrator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;
using Bifrost.DeadLetter;

using Microsoft.Extensions.Logging;

namespace Bifrost.Decorators;

/// <summary>
/// Decorator that observes admission outcomes (DR-6): rejected enqueues are
/// counted via <see cref="RejectionObserved"/> and routed to the dead-letter
/// pathway when DLQ infrastructure is configured — the same family as handler
/// failures — so shed work is never silently dropped on the floor.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <remarks>
/// <para>
/// <b>Routing is observability, not retry.</b> The caller always receives the
/// original rejected <see cref="EnqueueResult"/>; routing never re-enqueues,
/// never blocks admission, and never converts a rejection into an acceptance.
/// The at-least-once execution contract is unaffected: rejection happens at
/// admission, before the item enters the queue, so admitted work is still
/// executed at least once and rejected work was never admitted. Replaying a
/// dead-lettered rejection is a consumer decision, and consumer idempotency
/// obligations are unchanged — exactly as for handler-failure entries.
/// </para>
/// <para>
/// <b>Rejection-distinguishing marker.</b> Dead-letter entries produced here
/// carry <c>AttemptCount = 0</c> (never admitted, never attempted) and a cached
/// <see cref="WorkRejectedException"/> whose
/// <see cref="WorkRejectedException.Reason"/> is the admission
/// <see cref="RejectionReason"/> — distinguishing admission shed from handler
/// failure (which always has <c>AttemptCount &gt;= 1</c>).
/// </para>
/// <para>
/// <b>Shutdown rejections are counted but never dead-lettered.</b> During
/// teardown the orchestrator rejects with <see cref="RejectionReason.Shutdown"/>;
/// routing that noise into the DLQ would pollute replay with items the host
/// deliberately stopped admitting.
/// </para>
/// <para>
/// <b>Boolean enqueue surfaces.</b> <see cref="TryEnqueue"/> and
/// <see cref="TryRun"/> return <c>bool</c> and carry no
/// <see cref="RejectionReason"/>; a <c>false</c> result is inferred as
/// <see cref="RejectionReason.Shutdown"/> when the shutdown token is cancelled
/// and <see cref="RejectionReason.CapacityExceeded"/> otherwise. A drained (but
/// not stopped) orchestrator therefore reports boolean rejections as capacity —
/// a conservative approximation that routes rather than drops.
/// <see cref="Run"/> already surfaces its rejection to the caller as an
/// exception and is intentionally not routed.
/// </para>
/// </remarks>
internal sealed class RejectionRoutingOrchestrator<TWork> : IWorkOrchestrator<TWork>
{
    // Cached markers: rejections can be high-frequency under shed load, and the
    // marker is never thrown — one instance per reason, no per-rejection
    // allocation, no stack trace capture.
    private static readonly WorkRejectedException CapacityExceededMarker =
        new(RejectionReason.CapacityExceeded);

    private static readonly WorkRejectedException WatermarkExceededMarker =
        new(RejectionReason.WatermarkExceeded);

    private readonly IWorkOrchestrator<TWork> _inner;
    private readonly IDeadLetterQueue<TWork>? _dlq;
    private readonly DeadLetterNotifier<TWork>? _notifier;
    private readonly ILogger<RejectionRoutingOrchestrator<TWork>> _logger;
    private readonly Action<IOrchestratorEvent>? _eventPublishCallback;

    /// <summary>
    /// Initializes a new instance of the <see cref="RejectionRoutingOrchestrator{TWork}"/> class.
    /// </summary>
    /// <param name="inner">The inner orchestrator to wrap.</param>
    /// <param name="deadLetterQueue">
    /// The dead letter queue to route rejections to, or <c>null</c> when no DLQ
    /// infrastructure is configured (rejections are then observed but not routed).
    /// </param>
    /// <param name="notifier">
    /// The notifier for dead-letter events, or <c>null</c> when no DLQ
    /// infrastructure is configured.
    /// </param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="eventPublishCallback">
    /// Optional callback to publish <see cref="WorkDeadLetteredEvent{TWork}"/>
    /// events to the event stream.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="inner"/> or <paramref name="logger"/> is null.
    /// </exception>
    public RejectionRoutingOrchestrator(
        IWorkOrchestrator<TWork> inner,
        IDeadLetterQueue<TWork>? deadLetterQueue,
        DeadLetterNotifier<TWork>? notifier,
        ILogger<RejectionRoutingOrchestrator<TWork>> logger,
        Action<IOrchestratorEvent>? eventPublishCallback = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _dlq = deadLetterQueue;
        _notifier = notifier;
        _logger = logger;
        _eventPublishCallback = eventPublishCallback;
    }

    /// <summary>
    /// Gets the wrapped inner orchestrator. Exposed for instrumentation that
    /// needs to reach the concrete <see cref="WorkOrchestrator{TWork}"/> beneath
    /// this decorator (e.g., the queue-wait hook attach).
    /// </summary>
    internal IWorkOrchestrator<TWork> Inner => _inner;

    /// <summary>
    /// Gets or sets the rejection observation hook: invoked once per rejected
    /// enqueue with the work class and rejection reason, for every reason
    /// including <see cref="RejectionReason.Shutdown"/>. OpenTelemetry attaches
    /// the <c>bifrost.orchestrator.rejected</c> counter here (DR-3); rejections
    /// are always counted, independent of DLQ configuration.
    /// </summary>
    internal Action<WorkClass, RejectionReason>? RejectionObserved { get; set; }

    /// <inheritdoc/>
    public int PendingCount => _inner.PendingCount;

    /// <inheritdoc/>
    public int ActiveWorkers => _inner.ActiveWorkers;

    /// <inheritdoc/>
    public int Capacity => _inner.Capacity;

    /// <inheritdoc/>
    public async ValueTask<EnqueueResult> EnqueueAsync(TWork work, WorkClass workClass = WorkClass.Default, CancellationToken ct = default)
    {
        var result = await _inner.EnqueueAsync(work, workClass, ct).ConfigureAwait(false);

        // Reason is null on Accepted, and defensively skipped on malformed
        // results (e.g., default(EnqueueResult) from substitutes).
        if (!result.IsAccepted && result.Reason is { } reason)
        {
            RejectionObserved?.Invoke(workClass, reason);

            if (reason != RejectionReason.Shutdown && _dlq is not null)
            {
                await RouteToDeadLetterAsync(work, reason).ConfigureAwait(false);
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public bool TryEnqueue(TWork work, WorkClass workClass = WorkClass.Default)
    {
        if (_inner.TryEnqueue(work, workClass))
        {
            return true;
        }

        ObserveBooleanRejection(work, workClass);
        return false;
    }

    /// <inheritdoc/>
    public void Run(TWork work, WorkClass workClass = WorkClass.Default)
        => _inner.Run(work, workClass);

    /// <inheritdoc/>
    public bool TryRun(TWork work, WorkClass workClass = WorkClass.Default)
    {
        if (_inner.TryRun(work, workClass))
        {
            return true;
        }

        ObserveBooleanRejection(work, workClass);
        return false;
    }

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction()
        => _inner.CreateWorkerFunction();

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback)
        => _inner.CreateWorkerFunction(stateCallback);

    /// <inheritdoc/>
    public Task RequestScaleUpAsync(int count, CancellationToken cancellationToken = default)
        => _inner.RequestScaleUpAsync(count, cancellationToken);

    /// <inheritdoc/>
    public Task RequestScaleDownAsync(int count, CancellationToken cancellationToken = default)
        => _inner.RequestScaleDownAsync(count, cancellationToken);

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken ct = default)
        => _inner.StopAsync(ct);

    /// <inheritdoc/>
    public Task DrainAsync(CancellationToken ct = default)
        => _inner.DrainAsync(ct);

    /// <inheritdoc/>
    public CancellationToken GetShutdownToken()
        => _inner.GetShutdownToken();

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
        => _inner.DisposeAsync();

    /// <summary>
    /// Looks up the cached rejection marker for the given reason.
    /// </summary>
    /// <param name="reason">The non-shutdown rejection reason.</param>
    /// <returns>The cached marker exception carrying the reason.</returns>
    private static WorkRejectedException MarkerFor(RejectionReason reason)
        => reason == RejectionReason.WatermarkExceeded
            ? WatermarkExceededMarker
            : CapacityExceededMarker;

    /// <summary>
    /// Observes a rejection reported through a boolean enqueue surface, inferring
    /// the reason from the shutdown token (see class remarks), counting it, and
    /// routing non-shutdown rejections to the DLQ when configured.
    /// </summary>
    /// <param name="work">The rejected work item.</param>
    /// <param name="workClass">The work class it was enqueued under.</param>
    private void ObserveBooleanRejection(TWork work, WorkClass workClass)
    {
        var reason = _inner.GetShutdownToken().IsCancellationRequested
            ? RejectionReason.Shutdown
            : RejectionReason.CapacityExceeded;

        RejectionObserved?.Invoke(workClass, reason);

        if (reason == RejectionReason.Shutdown || _dlq is null)
        {
            return;
        }

        // The channel-backed DLQ completes synchronously, in which case this
        // helper runs to completion inline; an asynchronous custom
        // IDeadLetterQueue implementation completes off the synchronous caller
        // with error isolation (fire-and-forget, mirroring DeadLetterNotifier).
        _ = AwaitRouteAsync(RouteToDeadLetterAsync(work, reason));
    }

    /// <summary>
    /// Routes a rejected work item through the dead-letter machinery: persists
    /// the entry, notifies subscribers, and publishes the event-stream event.
    /// </summary>
    /// <param name="work">The rejected work item.</param>
    /// <param name="reason">The non-shutdown rejection reason.</param>
    /// <returns>A ValueTask that completes when the entry is persisted.</returns>
    private async ValueTask RouteToDeadLetterAsync(TWork work, RejectionReason reason)
    {
        var marker = MarkerFor(reason);

        var deadLettered = new DeadLetteredWork<TWork>(
            work,
            marker,
            AttemptCount: 0,
            DateTimeOffset.UtcNow,
            CorrelationId: null);

        // CancellationToken.None: dead-letter persistence is guaranteed even if
        // the producer's token is cancelled, mirroring DeadLetterHandler.
        await _dlq!.EnqueueAsync(deadLettered, CancellationToken.None).ConfigureAwait(false);

        var evt = new WorkDeadLetteredEvent<TWork>(work, marker, 0, DateTimeOffset.UtcNow);
        _notifier?.Notify(evt);

        try
        {
            _eventPublishCallback?.Invoke(evt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish dead-letter event for rejected work type {WorkType}", typeof(TWork).Name);
        }

        _logger.LogWarning("Work item rejected at admission ({Reason}) routed to dead-letter queue", reason);
    }

    /// <summary>
    /// Awaits a routing ValueTask with error isolation: completes inline when the
    /// route completed synchronously, and fire-and-forget otherwise, so boolean
    /// enqueue surfaces stay non-blocking.
    /// </summary>
    /// <param name="route">The pending routing ValueTask.</param>
    /// <returns>A Task representing the isolated await.</returns>
    private async Task AwaitRouteAsync(ValueTask route)
    {
        try
        {
            await route.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dead-letter rejected work item of type {WorkType}", typeof(TWork).Name);
        }
    }
}
