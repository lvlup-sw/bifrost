// =============================================================================
// <copyright file="IJobDispatcher.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// The port that runs a job's work when it fires. The scheduler owns timing and
/// persistence; a dispatcher owns how the fire is turned into work — enqueued on
/// the orchestrator, run inline, or handled by a custom strategy.
/// </summary>
public interface IJobDispatcher
{
    /// <summary>
    /// Dispatches a single job fire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>At-least-once delivery.</strong> Execution is at-least-once per
    /// scheduled occurrence. A process crash between the dispatch handoff and the
    /// subsequent <c>RecordFiredAsync</c> checkpoint leaves the durable
    /// <c>LastFiredAt</c> unchanged, so the scheduler treats the occurrence as
    /// missed on the next startup and re-fires it via the job's
    /// <see cref="MissedFirePolicy"/> (typically <c>Coalesce</c>). Make your
    /// handler idempotent. Use the pair
    /// (<see cref="JobFireContext.JobName"/>, <see cref="JobFireContext.FireTime"/>)
    /// as the deduplication key: the re-fire presents the same pair, so a handler
    /// that records or checks against that key will not double-process (DR-12).
    /// </para>
    /// <para>
    /// <strong>Duplicate window.</strong> The duplicate window is bounded: a
    /// <c>Coalesce</c> policy produces at most one catch-up fire per missed-fire
    /// gap, so a single crash yields at most one duplicate per occurrence.
    /// </para>
    /// </remarks>
    /// <param name="context">
    /// The fire context, carrying the job name, the scheduled occurrence time
    /// (the idempotency key with the job name), the next occurrence, and the
    /// scoped service provider.
    /// </param>
    /// <param name="ct">A token to cancel the dispatch.</param>
    /// <returns>A task that completes when the fire has been dispatched.</returns>
    ValueTask DispatchAsync(JobFireContext context, CancellationToken ct);
}
