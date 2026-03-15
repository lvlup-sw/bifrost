// =============================================================================
// <copyright file="WorkCompletedEvent.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core.Events;

/// <summary>
/// Event raised when work item processing completes.
/// </summary>
/// <typeparam name="TWork">The type of work item.</typeparam>
/// <param name="Work">The work item that was processed.</param>
/// <param name="Duration">The time taken to process the work item.</param>
/// <param name="Success">Whether processing completed successfully.</param>
/// <param name="CorrelationId">Optional correlation ID for filtering.</param>
/// <remarks>
/// This event is useful for monitoring work processing metrics
/// and tracking success/failure rates.
/// </remarks>
public sealed record WorkCompletedEvent<TWork>(
    TWork Work,
    TimeSpan Duration,
    bool Success,
    string? CorrelationId = null) : ICorrelatedEvent;