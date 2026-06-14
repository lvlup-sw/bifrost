// =============================================================================
// <copyright file="ITickHealthMonitor.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Observability;

/// <summary>
/// Tracks the liveness signals the scheduler health check reads (DR-8): the instant
/// of the most recent tick, and a rolling window of the most recent fire outcomes
/// (success or failure). The tick loop updates the monitor as it runs; the
/// <see cref="SchedulerHealthCheck"/> reads it to decide health.
/// </summary>
/// <remarks>
/// Implementations must be safe to read and update concurrently: the tick loop
/// updates the monitor from its own thread (and from pool-thread dispatch
/// completions), while the health check reads it from an arbitrary caller's thread.
/// </remarks>
internal interface ITickHealthMonitor
{
    /// <summary>
    /// Gets the instant of the most recent tick, or <see langword="null"/> when the
    /// loop has not yet ticked. The health check treats a stale value — older than its
    /// staleness threshold — as a stalled loop.
    /// </summary>
    DateTimeOffset? LastTickAt { get; }

    /// <summary>
    /// Gets the fraction of failures over the rolling window of recent fire outcomes,
    /// in the range [0, 1]. Returns <c>0</c> when the window is empty so a scheduler
    /// that has not yet fired is not penalised.
    /// </summary>
    double FailureRate { get; }

    /// <summary>
    /// Gets the number of fire outcomes currently held in the rolling window — at most
    /// the window size. The schedule inspector surfaces this as the recent fire count
    /// in its metrics snapshot.
    /// </summary>
    int RecentFireCount { get; }

    /// <summary>
    /// Records that the loop ticked at the given instant, updating
    /// <see cref="LastTickAt"/>.
    /// </summary>
    /// <param name="at">The tick instant (read from the injected clock — DR-7).</param>
    void RecordTick(DateTimeOffset at);

    /// <summary>
    /// Records the outcome of a single fire into the rolling window, evicting the
    /// oldest outcome once the window is full.
    /// </summary>
    /// <param name="success">
    /// <see langword="true"/> when the dispatch completed successfully;
    /// <see langword="false"/> when it failed.
    /// </param>
    void RecordFireOutcome(bool success);
}
