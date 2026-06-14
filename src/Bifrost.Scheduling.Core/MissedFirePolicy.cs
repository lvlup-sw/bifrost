// =============================================================================
// <copyright file="MissedFirePolicy.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// Determines how the scheduler reconciles occurrences that were missed while a
/// job could not fire — for example, after a process restart or a long pause.
/// </summary>
public enum MissedFirePolicy
{
    /// <summary>
    /// Collapse all missed occurrences into a single catch-up fire. This is the
    /// default (numeric value <c>0</c>), so an unset policy coalesces.
    /// </summary>
    Coalesce = 0,

    /// <summary>
    /// Fire once for every missed occurrence, replaying the full backlog in order.
    /// </summary>
    FireAllMissed,

    /// <summary>
    /// Skip every missed occurrence and resume from the next future occurrence.
    /// </summary>
    SkipMissed,
}
