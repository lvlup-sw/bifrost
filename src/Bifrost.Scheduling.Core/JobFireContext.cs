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
/// The scheduled occurrence time of this fire — the heap key, never a
/// wall-clock instant read at dispatch time (DR-12). This value is stable across
/// a re-fire of the same occurrence: if the process crashed between the dispatch
/// handoff and the <c>RecordFiredAsync</c> checkpoint, the scheduler re-fires the
/// same occurrence on startup and this field carries the same instant. Use the
/// pair (<paramref name="JobName"/>, <paramref name="FireTime"/>) as the
/// idempotency / deduplication key in your handler. For on-demand triggers (via
/// <c>TriggerAsync</c>) this is the trigger instant, since no scheduled
/// occurrence exists.
/// </param>
/// <param name="NextFireAt">
/// The job's next scheduled occurrence, or <see langword="null"/> when no
/// further occurrence is scheduled.
/// </param>
/// <param name="Services">
/// The service provider scoped to this fire, from which the dispatcher resolves
/// the work it runs. The scheduler opens a fresh <c>IServiceScope</c> per fire and
/// disposes it once the dispatch completes, so any scoped service resolved here lives
/// for exactly the duration of the fire. When the scheduler has no root provider to
/// scope from (a few low-level test fixtures) this is a no-op provider that returns
/// <see langword="null"/> for every service rather than throwing.
/// </param>
public readonly record struct JobFireContext(
    string JobName,
    DateTimeOffset FireTime,
    DateTimeOffset? NextFireAt,
    IServiceProvider Services);
