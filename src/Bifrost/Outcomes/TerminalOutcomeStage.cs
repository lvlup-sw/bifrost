// =============================================================================
// <copyright file="TerminalOutcomeStage.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;
using Bifrost.DeadLetter;

using Microsoft.Extensions.Logging;

namespace Bifrost.Outcomes;

/// <summary>
/// Settles each started work item once, after its handler pipeline exits and its key slot is
/// released, and counts items that are abandoned.
/// </summary>
/// <typeparam name="TWork">The work item type.</typeparam>
/// <remarks>
/// <para>
/// The stage has fixed slots in <see cref="WorkOrchestrator{TWork}"/>. The orchestrator runs the
/// attempt loop (<see cref="MaxAttempts"/>) outside every handler decorator, so each decorator and
/// each scoped handler sees every attempt. After the last attempt, the scope is disposed and the key
/// slot is released. Then <see cref="SettleAsync"/> records the outcome, captures a final failure in
/// the dead-letter queue, notifies subscribers, publishes events, and calls
/// <see cref="Observed"/>. Last, the worker reports idle.
/// </para>
/// <para>
/// Settling never throws. A dead-letter queue that throws is counted as a capture error and logged.
/// A failing event publisher or observer is logged. The worker always reaches its idle report.
/// </para>
/// </remarks>
internal sealed class TerminalOutcomeStage<TWork>
{
    private readonly IDeadLetterQueue<TWork>? _deadLetterQueue;
    private readonly DeadLetterNotifier<TWork>? _notifier;
    private readonly Action<IOrchestratorEvent>? _publish;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the stage from its fixed services.</summary>
    /// <param name="options">The configured services, or <see langword="null"/> for one attempt and no capture.</param>
    /// <param name="logger">The orchestrator's logger.</param>
    /// <param name="timeProvider">The source of dead-letter timestamps.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the dead-letter retry count is negative.</exception>
    internal TerminalOutcomeStage(TerminalOutcomeOptions<TWork>? options, ILogger logger, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _deadLetterQueue = options?.DeadLetterQueue;
        _notifier = options?.Notifier;
        _publish = options?.EventPublisher;
        _logger = logger;
        _timeProvider = timeProvider;
        Counters = options?.Counters ?? new TerminalOutcomeCounters<TWork>();
        var retries = options?.DeadLetterRetries ?? 0;
        ArgumentOutOfRangeException.ThrowIfNegative(retries, nameof(options));
        MaxAttempts = _deadLetterQueue is null ? 1 : retries + 1;
    }

    /// <summary>
    /// Gets the number of handler attempts one item may use: one, or one plus the dead-letter
    /// retries when a dead-letter queue is configured.
    /// </summary>
    internal int MaxAttempts { get; }

    /// <summary>Gets the aggregate counters.</summary>
    internal TerminalOutcomeCounters<TWork> Counters { get; }

    /// <summary>
    /// Gets or sets an observer that is called once per settled item, after the counters change.
    /// Abandoned items are counted in bulk and do not reach the observer.
    /// </summary>
    internal Action<TerminalOutcomeRecord>? Observed { get; set; }

    /// <summary>Logs a failed attempt that the orchestrator will retry.</summary>
    /// <param name="error">The attempt's exception.</param>
    /// <param name="attempt">The one-based number of the failed attempt.</param>
    internal void ReportRetry(Exception error, int attempt)
        => _logger.LogWarning(error, "Work item failed on attempt {Attempt}/{MaxAttempts}", attempt, MaxAttempts);

    /// <summary>Counts accepted items that no worker will start.</summary>
    /// <param name="count">The number of abandoned items.</param>
    internal void RecordAbandoned(int count)
    {
        if (count > 0) Counters.RecordAbandoned(count);
    }

    /// <summary>Settles one started item.</summary>
    /// <param name="work">The work item.</param>
    /// <param name="outcome">The final outcome: succeeded, failed, or canceled.</param>
    /// <param name="attempts">The number of handler attempts the item used.</param>
    /// <param name="error">The last attempt's exception, or <see langword="null"/> on success.</param>
    /// <param name="duration">The time across all attempts.</param>
    /// <returns>A task that completes when the item is settled. It completes synchronously unless a capture is awaited.</returns>
    /// <remarks>
    /// Only a <see cref="TerminalOutcome.Failed"/> item is captured, exactly once, with
    /// <see cref="CancellationToken.None"/> so a host shutdown cannot skip the write.
    /// </remarks>
    internal ValueTask SettleAsync(TWork work, TerminalOutcome outcome, int attempts, Exception? error, TimeSpan duration)
    {
        Counters.RecordAttempts(attempts);
        if (outcome == TerminalOutcome.Failed && _deadLetterQueue is not null)
            return SettleCapturedAsync(work, attempts, error, duration);

        Finish(work, outcome, attempts, duration, CaptureDisposition.None);
        return ValueTask.CompletedTask;
    }

    private async ValueTask SettleCapturedAsync(TWork work, int attempts, Exception? error, TimeSpan duration)
    {
        var disposition = await CaptureAsync(work, attempts, error);
        Finish(work, TerminalOutcome.Failed, attempts, duration, disposition);
    }

    /// <summary>Writes one final failure to the dead-letter queue and announces it.</summary>
    /// <param name="work">The work item.</param>
    /// <param name="attempts">The number of handler attempts.</param>
    /// <param name="error">The last attempt's exception.</param>
    /// <returns>The capture disposition.</returns>
    /// <remarks>
    /// Subscribers and the event stream hear about the failure only after the write succeeds, so a
    /// notification always matches a stored entry.
    /// </remarks>
    private async ValueTask<CaptureDisposition> CaptureAsync(TWork work, int attempts, Exception? error)
    {
        var timestamp = _timeProvider.GetUtcNow();
        try
        {
            await _deadLetterQueue!.EnqueueAsync(
                new DeadLetteredWork<TWork>(work, error, attempts, timestamp, CorrelationId: null),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Counters.RecordCaptureError();
            _logger.LogError(ex, "Failed to dead-letter a work item of type {WorkType} after {Attempts} attempts", typeof(TWork).Name, attempts);
            return CaptureDisposition.CaptureFailed;
        }

        Counters.RecordCaptured();
        _logger.LogWarning("Work item of type {WorkType} dead-lettered after {Attempts} attempts", typeof(TWork).Name, attempts);
        var evt = new WorkDeadLetteredEvent<TWork>(work, error, attempts, timestamp);
        _notifier?.Notify(evt);
        Publish(evt);
        return CaptureDisposition.Captured;
    }

    private void Finish(TWork work, TerminalOutcome outcome, int attempts, TimeSpan duration, CaptureDisposition capture)
    {
        Counters.RecordOutcome(outcome);
        if (_publish is not null) Publish(new WorkCompletedEvent<TWork>(work, duration, outcome == TerminalOutcome.Succeeded));
        if (Observed is { } observer) Observe(observer, new TerminalOutcomeRecord(outcome, attempts, duration, capture));
    }

    private void Publish(IOrchestratorEvent evt)
    {
        if (_publish is null) return;
        try
        {
            _publish(evt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish {EventType} for work type {WorkType}", evt.GetType().Name, typeof(TWork).Name);
        }
    }

    private void Observe(Action<TerminalOutcomeRecord> observer, TerminalOutcomeRecord record)
    {
        try
        {
            observer(record);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Terminal outcome observer failed for work type {WorkType}", typeof(TWork).Name);
        }
    }
}
