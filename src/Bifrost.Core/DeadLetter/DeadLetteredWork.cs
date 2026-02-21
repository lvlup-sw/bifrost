// =============================================================================
// <copyright file="DeadLetteredWork.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core.DeadLetter;

/// <summary>
/// Represents a work item that has been dead-lettered after exhausting all retry attempts.
/// </summary>
/// <typeparam name="TWork">The type of work item.</typeparam>
/// <param name="Work">The original work item that failed.</param>
/// <param name="Exception">The exception that caused the final failure, if any.</param>
/// <param name="AttemptCount">The total number of processing attempts made.</param>
/// <param name="FailedAt">The timestamp when the work item was dead-lettered.</param>
/// <param name="CorrelationId">Optional correlation ID for tracing.</param>
public readonly record struct DeadLetteredWork<TWork>(
    TWork Work,
    Exception? Exception,
    int AttemptCount,
    DateTimeOffset FailedAt,
    string? CorrelationId);
