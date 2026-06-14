// =============================================================================
// <copyright file="ScheduleMetricsSnapshot.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// A point-in-time read-only snapshot of scheduler metric values, returned by
/// <see cref="IBifrostScheduleInspector.GetMetricsSnapshot"/> (DR-8). It surfaces a
/// small, cheap set of current counts for diagnostics and dashboards without
/// requiring a metrics exporter to be wired up.
/// </summary>
/// <param name="JobCount">The number of jobs currently registered with the scheduler.</param>
/// <param name="RecentFireCount">
/// The number of fires in the scheduler's recent rolling window — at most the
/// window size. Zero when the scheduler has not yet fired or no fire source is
/// attached.
/// </param>
public readonly record struct ScheduleMetricsSnapshot(
    int JobCount,
    int RecentFireCount);
