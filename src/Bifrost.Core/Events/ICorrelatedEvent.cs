// =============================================================================
// <copyright file="ICorrelatedEvent.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core.Events;

/// <summary>
/// Interface for events that support correlation ID filtering.
/// </summary>
/// <remarks>
/// Events implementing this interface can be filtered by correlation ID
/// when subscribing to event streams. This enables filtering events
/// for specific workflows or operations.
/// </remarks>
public interface ICorrelatedEvent : IOrchestratorEvent
{
    /// <summary>
    /// Gets the correlation ID associated with this event.
    /// </summary>
    /// <value>
    /// The correlation ID, or <c>null</c> if no correlation ID is set.
    /// </value>
    string? CorrelationId { get; }
}
