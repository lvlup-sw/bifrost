// =============================================================================
// <copyright file="WorkDeadLetteredEvent.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core.Events;

/// <summary>
/// Event raised when a work item is dead-lettered after exhausting all retries.
/// </summary>
/// <typeparam name="TWork">The type of work item.</typeparam>
/// <param name="Work">The work item that was dead-lettered.</param>
/// <param name="Exception">The exception that caused the final failure, if any.</param>
/// <param name="AttemptCount">The total number of processing attempts made.</param>
/// <param name="Timestamp">The timestamp when the work was dead-lettered.</param>
/// <param name="CorrelationId">Optional correlation ID for filtering.</param>
public sealed record WorkDeadLetteredEvent<TWork>(
    TWork Work,
    Exception? Exception,
    int AttemptCount,
    DateTimeOffset Timestamp,
    string? CorrelationId = null) : ICorrelatedEvent;