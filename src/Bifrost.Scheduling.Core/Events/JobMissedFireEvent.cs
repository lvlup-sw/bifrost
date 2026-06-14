// =============================================================================
// <copyright file="JobMissedFireEvent.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core.Events;

/// <summary>
/// Raised when the scheduler reconciles occurrences that were missed while a job
/// could not fire, reporting how many were missed and the policy applied.
/// </summary>
/// <param name="JobName">The name of the job with missed occurrences.</param>
/// <param name="MissedCount">The number of occurrences that were missed.</param>
/// <param name="Policy">The policy applied to reconcile the missed occurrences.</param>
public readonly record struct JobMissedFireEvent(
    string JobName,
    int MissedCount,
    MissedFirePolicy Policy);
