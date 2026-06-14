// =============================================================================
// <copyright file="JobState.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// The lifecycle state of a scheduled job as tracked by the scheduler and
/// persisted in the schedule store.
/// </summary>
public enum JobState
{
    /// <summary>
    /// The job is active: its cadence is being evaluated and it fires when due.
    /// </summary>
    Running,

    /// <summary>
    /// The job is registered but suspended: its cadence is not evaluated and it
    /// does not fire until resumed.
    /// </summary>
    Paused,

    /// <summary>
    /// The job entered a fault state — for example, its cadence could not be
    /// evaluated — and will not fire until it is repaired or re-registered.
    /// </summary>
    Faulted,

    /// <summary>
    /// The job has run to completion: its cadence has no further occurrences and
    /// it will not fire again.
    /// </summary>
    Completed,
}
