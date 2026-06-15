// =============================================================================
// <copyright file="SchedulerTestHarness.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Scheduling.Testing;

/// <summary>
/// The default implementation of <see cref="ISchedulerTestHarness"/> (DR-9):
/// drives the <see cref="ScheduleTickLoop"/> deterministically by combining a
/// <see cref="FakeTimeProvider"/> clock advance with the loop's internal
/// quiescence barrier (<c>WaitForIdleAsync</c>).
/// </summary>
/// <remarks>
/// <para>
/// Construct via <c>AddSchedulerTesting</c> (which registers both the
/// <see cref="FakeTimeProvider"/> and the harness in DI), or directly in unit
/// tests that wire the loop by hand — pass the same <see cref="FakeTimeProvider"/>
/// that was given to the <see cref="ScheduleTickLoop"/>.
/// </para>
/// <para>
/// The harness requires a <see cref="FakeTimeProvider"/> because time control is
/// the mechanism by which jobs are made to fire deterministically; a real
/// <see cref="TimeProvider"/> would cause the loop to sleep against real wall-clock
/// time and break the determinism guarantee. Passing a real provider throws
/// <see cref="ArgumentException"/> at construction time.
/// </para>
/// </remarks>
public sealed class SchedulerTestHarness : ISchedulerTestHarness
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(10);

    private readonly ScheduleTickLoop loop;
    private readonly FakeTimeProvider fakeTime;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchedulerTestHarness"/> class.
    /// </summary>
    /// <param name="loop">
    /// The tick loop to drive. The loop must have been constructed with the same
    /// <see cref="FakeTimeProvider"/> instance as <paramref name="timeProvider"/>.
    /// </param>
    /// <param name="timeProvider">
    /// The fake time provider the loop uses. Must be a
    /// <see cref="FakeTimeProvider"/> instance — a real <see cref="TimeProvider"/>
    /// is rejected with <see cref="ArgumentException"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="loop"/> or <paramref name="timeProvider"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="timeProvider"/> is not a <see cref="FakeTimeProvider"/>. A
    /// real provider cannot drive deterministic scheduling tests.
    /// </exception>
    public SchedulerTestHarness(ScheduleTickLoop loop, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (timeProvider is not FakeTimeProvider fake)
        {
            throw new ArgumentException(
                $"The scheduler test harness requires a {nameof(FakeTimeProvider)}. " +
                $"A real {nameof(TimeProvider)} cannot drive deterministic scheduling tests. " +
                $"Use AddSchedulerTesting() or supply a {nameof(FakeTimeProvider)} when " +
                $"constructing the tick loop.",
                nameof(timeProvider));
        }

        this.loop = loop;
        this.fakeTime = fake;
    }

    /// <inheritdoc/>
    public async Task AdvanceAsync(TimeSpan duration)
    {
        // Advance the fake clock. This fires the FakeTimeProvider's internal timers,
        // which wakes Task.Delay calls inside the tick loop that are waiting on the
        // fake clock, causing the loop to dispatch any jobs that are now due.
        this.fakeTime.Advance(duration);

        // Wait until the loop has drained all commands, dispatched every due job, and
        // all in-flight dispatches have completed.
        await this.loop.WaitForIdleAsync(DefaultIdleTimeout).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task FireDueJobsAsync()
    {
        // Advance by zero to wake the tick loop without moving the clock, causing it
        // to re-evaluate which jobs are due at the current instant and dispatch them.
        this.fakeTime.Advance(TimeSpan.Zero);

        await this.loop.WaitForIdleAsync(DefaultIdleTimeout).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task WaitForIdleAsync(TimeSpan timeout)
        => this.loop.WaitForIdleAsync(timeout);
}
