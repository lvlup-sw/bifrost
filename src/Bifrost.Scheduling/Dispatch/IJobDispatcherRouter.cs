// =============================================================================
// <copyright file="IJobDispatcherRouter.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.Dispatch;

/// <summary>
/// The dispatch executor: given a job's live <see cref="IJobDispatcher"/> and a
/// <see cref="JobFireContext"/>, it hands the fire off to a thread-pool thread,
/// invokes the dispatcher, and isolates a throwing dispatcher so it can never
/// crash the caller.
/// </summary>
/// <remarks>
/// The tick loop owns timing and must never await user code on its own thread:
/// it calls <see cref="Dispatch"/> as fire-and-forget and returns immediately,
/// while the actual <see cref="IJobDispatcher.DispatchAsync"/> runs on the pool.
/// A dispatcher that throws is surfaced as a
/// <see cref="Bifrost.Scheduling.Core.Events.JobFireFailedEvent"/> through the
/// event sink rather than propagated.
/// </remarks>
internal interface IJobDispatcherRouter
{
    /// <summary>
    /// Hands a single job fire off to a pool thread and invokes
    /// <paramref name="dispatcher"/>. Returns immediately; the dispatch runs
    /// asynchronously. A dispatcher that throws is recorded as a failed fire
    /// rather than propagated.
    /// </summary>
    /// <param name="dispatcher">The job's live dispatcher.</param>
    /// <param name="context">The fire context for this occurrence.</param>
    /// <param name="ct">A token to cancel the dispatch.</param>
    void Dispatch(IJobDispatcher dispatcher, JobFireContext context, CancellationToken ct);
}
