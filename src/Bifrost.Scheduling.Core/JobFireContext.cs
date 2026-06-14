// =============================================================================
// <copyright file="JobFireContext.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// The immutable payload handed to an <see cref="IJobDispatcher"/> when a job
/// fires.
/// </summary>
/// <param name="JobName">The name of the job that fired.</param>
/// <param name="FireTime">
/// The scheduled occurrence time of this fire. This is the occurrence's logical
/// instant, not the wall-clock instant the dispatch ran, so it is stable across
/// a re-fire of the same occurrence; the pair
/// (<paramref name="JobName"/>, <paramref name="FireTime"/>) is the idempotency
/// key a dispatcher uses to deduplicate retried occurrences.
/// </param>
/// <param name="NextFireAt">
/// The job's next scheduled occurrence, or <see langword="null"/> when no
/// further occurrence is scheduled.
/// </param>
/// <param name="Services">
/// The service provider scoped to this fire, from which the dispatcher resolves
/// the work it runs.
/// </param>
public readonly record struct JobFireContext(
    string JobName,
    DateTimeOffset FireTime,
    DateTimeOffset? NextFireAt,
    IServiceProvider Services);
