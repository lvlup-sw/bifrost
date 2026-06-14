// =============================================================================
// <copyright file="ISchedulerFaultSource.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Observability;

/// <summary>
/// Exposes the scheduler's faulted state to the health check without coupling it to
/// the full tick loop. The <c>ScheduleTickLoop</c> implements this, surfacing its
/// <c>IsFaulted</c> flag (DR-10): the loop crashed too many times in its restart
/// window and stopped ticking.
/// </summary>
internal interface ISchedulerFaultSource
{
    /// <summary>
    /// Gets a value indicating whether the scheduler has transitioned to its faulted
    /// state and stopped ticking.
    /// </summary>
    bool IsFaulted { get; }
}
