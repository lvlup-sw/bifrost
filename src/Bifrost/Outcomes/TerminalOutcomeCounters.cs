// =============================================================================
// <copyright file="TerminalOutcomeCounters.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Outcomes;

/// <summary>Aggregate counts of terminal outcomes, handler attempts, and dead-letter activity.</summary>
/// <typeparam name="TWork">The work item type. It gives each work type its own DI singleton.</typeparam>
/// <remarks>
/// <para>
/// The counts are process-local, lock-free, and never labeled with work items or keys. Each count
/// is separate: a captured failure adds one to <see cref="Failed"/> and one to
/// <see cref="DeadLetterCaptured"/>, and a dead-letter retry adds to <see cref="HandlerAttempts"/>
/// only.
/// </para>
/// <para>
/// <see cref="DeadLetterCaptured"/> and <see cref="DeadLetterCaptureErrors"/> count final handler
/// failures only. <see cref="DeadLetterEvictions"/> and <see cref="DeadLetterNotificationsDropped"/>
/// count every entry in the built-in queue and notifier, including admission rejections that the
/// rejection-routing decorator writes there.
/// </para>
/// <para>
/// Subscriber drops here mean dropped dead-letter notifications only
/// (<see cref="DeadLetterNotificationsDropped"/>). Events that an event-stream subscriber channel
/// overwrites (its full mode is drop-oldest) or loses when a stale subscriber is removed are not
/// counted.
/// </para>
/// </remarks>
internal sealed class TerminalOutcomeCounters<TWork>
{
    private long _succeeded;
    private long _failed;
    private long _canceled;
    private long _abandoned;
    private long _handlerAttempts;
    private long _deadLetterCaptured;
    private long _deadLetterCaptureErrors;
    private long _deadLetterEvictions;
    private long _deadLetterNotificationsDropped;

    /// <summary>Gets the number of items whose last attempt succeeded.</summary>
    internal long Succeeded => Volatile.Read(ref _succeeded);

    /// <summary>Gets the number of items whose last attempt failed.</summary>
    internal long Failed => Volatile.Read(ref _failed);

    /// <summary>Gets the number of items whose handler token was cancelled.</summary>
    internal long Canceled => Volatile.Read(ref _canceled);

    /// <summary>Gets the number of accepted items that no worker will start.</summary>
    internal long Abandoned => Volatile.Read(ref _abandoned);

    /// <summary>Gets the total number of handler attempts, including dead-letter retries.</summary>
    internal long HandlerAttempts => Volatile.Read(ref _handlerAttempts);

    /// <summary>Gets the number of final handler failures written to the dead-letter queue.</summary>
    internal long DeadLetterCaptured => Volatile.Read(ref _deadLetterCaptured);

    /// <summary>Gets the number of final handler failures the dead-letter queue failed to store.</summary>
    internal long DeadLetterCaptureErrors => Volatile.Read(ref _deadLetterCaptureErrors);

    /// <summary>Gets the number of entries the built-in dead-letter queue dropped because it was full.</summary>
    internal long DeadLetterEvictions => Volatile.Read(ref _deadLetterEvictions);

    /// <summary>Gets the number of subscriber notifications the dead-letter notifier dropped.</summary>
    internal long DeadLetterNotificationsDropped => Volatile.Read(ref _deadLetterNotificationsDropped);

    /// <summary>Counts one settled outcome.</summary>
    /// <param name="outcome">The outcome.</param>
    internal void RecordOutcome(TerminalOutcome outcome)
    {
        switch (outcome)
        {
            case TerminalOutcome.Succeeded:
                Interlocked.Increment(ref _succeeded);
                break;
            case TerminalOutcome.Failed:
                Interlocked.Increment(ref _failed);
                break;
            case TerminalOutcome.Canceled:
                Interlocked.Increment(ref _canceled);
                break;
            case TerminalOutcome.Abandoned:
                Interlocked.Increment(ref _abandoned);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    /// <summary>Counts the handler attempts one item used.</summary>
    /// <param name="count">The number of attempts.</param>
    internal void RecordAttempts(int count) => Interlocked.Add(ref _handlerAttempts, count);

    /// <summary>Counts one final failure written to the dead-letter queue.</summary>
    internal void RecordCaptured() => Interlocked.Increment(ref _deadLetterCaptured);

    /// <summary>Counts one final failure the dead-letter queue failed to store.</summary>
    internal void RecordCaptureError() => Interlocked.Increment(ref _deadLetterCaptureErrors);

    /// <summary>Counts one entry the built-in dead-letter queue dropped.</summary>
    internal void RecordEviction() => Interlocked.Increment(ref _deadLetterEvictions);

    /// <summary>Counts one subscriber notification the dead-letter notifier dropped.</summary>
    internal void RecordNotificationDropped() => Interlocked.Increment(ref _deadLetterNotificationsDropped);
}
