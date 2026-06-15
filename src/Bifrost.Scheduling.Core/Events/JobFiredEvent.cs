// =============================================================================
// <copyright file="JobFiredEvent.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core.Events;

/// <summary>
/// Raised when a job fires and its work is successfully dispatched.
/// </summary>
/// <param name="JobName">The name of the job that fired.</param>
/// <param name="FiredAt">The instant the job fired.</param>
/// <param name="NextFireAt">
/// The job's next scheduled occurrence, or <see langword="null"/> when no
/// further occurrence is scheduled.
/// </param>
public readonly record struct JobFiredEvent(
    string JobName,
    DateTimeOffset FiredAt,
    DateTimeOffset? NextFireAt);
