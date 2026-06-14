// =============================================================================
// <copyright file="SchedulerFaultedEvent.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core.Events;

/// <summary>
/// Raised when the scheduler itself faults — an error in the scheduling loop
/// rather than in an individual job fire.
/// </summary>
/// <param name="Exception">The exception that faulted the scheduler.</param>
/// <param name="FaultedAt">The instant the scheduler faulted.</param>
public readonly record struct SchedulerFaultedEvent(
    Exception Exception,
    DateTimeOffset FaultedAt);
