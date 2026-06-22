// =============================================================================
// <copyright file="SchedulerOptions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling;

/// <summary>
/// Configuration for the scheduler's tick loop. Tunes the behaviours the loop
/// cannot derive from a job's cadence — multi-instance expectations, the shutdown
/// grace window, and the loop's fault-recovery thresholds.
/// </summary>
public sealed class SchedulerOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the host expects more than one
    /// scheduler instance to run against the same store.
    /// </summary>
    /// <remarks>
    /// Bifrost scheduling is unconditionally always-leader (DR-6): every instance
    /// ticks every registered job. With the default non-exclusive store this means
    /// running on more than one process produces duplicate fires. Setting this to
    /// <see langword="true"/> acknowledges that expectation and trades the duplicate
    /// fires for a prominent startup warning instead of silent duplication.
    /// Multi-instance coordination (leases, leadership) arrives with the storage
    /// adapter; until then, run Bifrost scheduling on exactly one process.
    /// </remarks>
    public bool MultiInstanceExpected { get; set; }

    /// <summary>
    /// Gets or sets the maximum time graceful shutdown waits for in-flight job
    /// dispatches to complete before abandoning them (DR-10).
    /// </summary>
    /// <remarks>
    /// On <see cref="Microsoft.Extensions.Hosting.IHostedService.StopAsync"/> the
    /// loop stops scheduling new fires and waits up to this window for the dispatches
    /// already handed off to the pool to finish. Dispatches still running when the
    /// window elapses are abandoned so shutdown is never blocked indefinitely.
    /// </remarks>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum number of consecutive tick-loop restarts allowed
    /// within <see cref="RestartWindow"/> before the scheduler transitions to its
    /// faulted state and stops ticking (DR-10).
    /// </summary>
    /// <remarks>
    /// A fault in the loop's own code is caught, logged, surfaced as a
    /// <see cref="Bifrost.Scheduling.Core.Events.SchedulerFaultedEvent"/>, and the
    /// loop restarts. If the loop fails this many times within the window — a tight
    /// crash loop rather than a transient fault — it gives up rather than spinning.
    /// </remarks>
    public int MaxRestartsInWindow { get; set; } = 3;

    /// <summary>
    /// Gets or sets the sliding window over which consecutive restarts are counted
    /// against <see cref="MaxRestartsInWindow"/> (DR-10).
    /// </summary>
    public TimeSpan RestartWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the delay the loop waits before re-entering the tick loop after a
    /// repeated fault, so a crash loop backs off instead of spinning the CPU at full tilt
    /// until <see cref="MaxRestartsInWindow"/> is reached (DR-10).
    /// </summary>
    /// <remarks>
    /// The delay is measured on the injected <see cref="TimeProvider"/>, not the wall
    /// clock, and is cancelled by shutdown — cancelling during the backoff exits the loop
    /// cleanly as a normal stop. It is skipped for an isolated first fault in the restart
    /// window (a one-off blip recovers immediately) and applies only from the second
    /// consecutive fault onward — the same crash-loop signal counted against
    /// <see cref="MaxRestartsInWindow"/>. The give-up transition (restart count exceeding
    /// <see cref="MaxRestartsInWindow"/> within <see cref="RestartWindow"/>) is unaffected,
    /// and that final iteration does not back off. Set to <see cref="TimeSpan.Zero"/> to
    /// restart immediately (the pre-backoff behaviour). The default is one second: short
    /// enough to recover promptly, long enough to keep a crash loop from saturating a core.
    /// </remarks>
    public TimeSpan RestartBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
