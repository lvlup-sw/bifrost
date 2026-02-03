// =============================================================================
// <copyright file="WorkEnqueuedEvent.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core.Events;

/// <summary>
/// Event raised when work is enqueued to the orchestrator.
/// </summary>
/// <typeparam name="TWork">The type of work item.</typeparam>
/// <param name="Work">The work item that was enqueued.</param>
/// <param name="Timestamp">The timestamp when the work was enqueued.</param>
/// <param name="QueueDepth">The queue depth after enqueueing.</param>
/// <param name="CorrelationId">Optional correlation ID for filtering.</param>
/// <remarks>
/// This event is useful for monitoring queue behavior and
/// implementing custom backpressure strategies.
/// </remarks>
public readonly record struct WorkEnqueuedEvent<TWork>(
    TWork Work,
    DateTimeOffset Timestamp,
    int QueueDepth,
    string? CorrelationId = null) : ICorrelatedEvent;
