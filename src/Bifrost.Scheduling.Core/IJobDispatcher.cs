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
    /// <param name="context">
    /// The fire context, carrying the job name, the scheduled occurrence time
    /// (the idempotency key with the job name), the next occurrence, and the
    /// scoped service provider.
    /// </param>
    /// <param name="ct">A token to cancel the dispatch.</param>
    /// <returns>A task that completes when the fire has been dispatched.</returns>
    ValueTask DispatchAsync(JobFireContext context, CancellationToken ct);
}
