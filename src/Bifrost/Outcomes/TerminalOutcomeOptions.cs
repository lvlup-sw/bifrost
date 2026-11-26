// =============================================================================
// <copyright file="TerminalOutcomeOptions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;
using Bifrost.DeadLetter;

namespace Bifrost.Outcomes;

/// <summary>The fixed services of the terminal outcome stage, set once when the orchestrator is built.</summary>
/// <typeparam name="TWork">The work item type.</typeparam>
/// <remarks>
/// The builder fills this from flags, not from handler decorator positions, so the stage does the
/// same thing for every call order of <c>WithDeadLetterQueue</c>, <c>WithEventStream</c>, and
/// <c>WithHandlerDecorator</c>.
/// </remarks>
internal sealed class TerminalOutcomeOptions<TWork>
{
    /// <summary>
    /// Gets the dead-letter queue that receives final handler failures, or <see langword="null"/>
    /// when dead-lettering is not configured.
    /// </summary>
    internal IDeadLetterQueue<TWork>? DeadLetterQueue { get; init; }

    /// <summary>
    /// Gets the number of immediate retries after the first attempt. It is used only when
    /// <see cref="DeadLetterQueue"/> is set.
    /// </summary>
    internal int DeadLetterRetries { get; init; }

    /// <summary>Gets the notifier for dead-letter subscribers, or <see langword="null"/>.</summary>
    internal DeadLetterNotifier<TWork>? Notifier { get; init; }

    /// <summary>
    /// Gets the event-stream publisher, or <see langword="null"/> when no event stream is
    /// configured. When it is set, the stage publishes one <see cref="WorkCompletedEvent{TWork}"/>
    /// per started item and one <see cref="WorkDeadLetteredEvent{TWork}"/> per captured failure.
    /// </summary>
    internal Action<IOrchestratorEvent>? EventPublisher { get; init; }

    /// <summary>
    /// Gets the shared counters, or <see langword="null"/> to let the stage create its own.
    /// </summary>
    internal TerminalOutcomeCounters<TWork>? Counters { get; init; }
}
