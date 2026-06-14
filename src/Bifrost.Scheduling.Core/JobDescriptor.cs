// =============================================================================
// <copyright file="JobDescriptor.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// A read-only projection of a registered job's current state, returned by the
/// schedule registry's inspection methods.
/// </summary>
/// <param name="Name">The unique job name.</param>
/// <param name="State">The job's current lifecycle state.</param>
/// <param name="Cadence">The schedule that determines when the job fires.</param>
/// <param name="LastFiredAt">
/// The instant the job most recently fired, or <see langword="null"/> if it has
/// never fired.
/// </param>
/// <param name="NextFireAt">
/// The instant the job is next due to fire, or <see langword="null"/> if no
/// further occurrence is scheduled.
/// </param>
/// <param name="IsRunning">
/// Whether a fire of this job is currently in flight. This is distinct from
/// <paramref name="State"/>: a job can be in the <see cref="JobState.Running"/>
/// state (active and scheduled) while not having a dispatch in flight.
/// </param>
public readonly record struct JobDescriptor(
    string Name,
    JobState State,
    Cadence Cadence,
    DateTimeOffset? LastFiredAt,
    DateTimeOffset? NextFireAt,
    bool IsRunning);
