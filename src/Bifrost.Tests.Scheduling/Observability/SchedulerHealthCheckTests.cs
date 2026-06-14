// =============================================================================
// <copyright file="SchedulerHealthCheckTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Observability;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.Observability;

/// <summary>
/// Tests for <see cref="SchedulerHealthCheck"/> (Task 33, DR-8): the check is
/// <see cref="HealthStatus.Healthy"/> when the loop is ticking and dispatching
/// successfully, and <see cref="HealthStatus.Unhealthy"/> on a stalled tick, an
/// excessive dispatch-failure rate, or the scheduler's faulted state. Time flows
/// through a <see cref="FakeTimeProvider"/> so staleness is deterministic.
/// </summary>
public sealed class SchedulerHealthCheckTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan ExpectedInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Verifies a recently-ticked, successfully-dispatching, non-faulted scheduler is
    /// reported healthy.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RunningNormally_IsHealthy()
    {
        var time = new FakeTimeProvider(Start);
        var monitor = new TickHealthMonitor();
        monitor.RecordTick(time.GetUtcNow());
        for (var i = 0; i < 10; i++)
        {
            monitor.RecordFireOutcome(success: true);
        }

        var check = NewCheck(monitor, faulted: false, time);

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    }

    /// <summary>
    /// Verifies the check is unhealthy when no tick has occurred for more than three
    /// times the expected interval.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task NoTickInThreeIntervals_IsUnhealthy()
    {
        var time = new FakeTimeProvider(Start);
        var monitor = new TickHealthMonitor();
        monitor.RecordTick(time.GetUtcNow());

        var check = NewCheck(monitor, faulted: false, time);

        // Advance past 3x the expected interval with no further tick recorded.
        time.Advance(TimeSpan.FromTicks((ExpectedInterval.Ticks * 3) + 1));

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    }

    /// <summary>
    /// Verifies the check is unhealthy when the dispatch-failure rate over the rolling
    /// window of the last 100 fires exceeds 50%.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchFailureRateOverHalf_IsUnhealthy()
    {
        var time = new FakeTimeProvider(Start);
        var monitor = new TickHealthMonitor();
        monitor.RecordTick(time.GetUtcNow());

        // 60 failures, 40 successes over the last 100 fires => 60% > 50%.
        for (var i = 0; i < 60; i++)
        {
            monitor.RecordFireOutcome(success: false);
        }

        for (var i = 0; i < 40; i++)
        {
            monitor.RecordFireOutcome(success: true);
        }

        var check = NewCheck(monitor, faulted: false, time);

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    }

    /// <summary>
    /// Verifies the check reports the rolling window as the last 100 fires only: an
    /// early burst of failures rolls out of the window once 100 later successes arrive.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FailuresOlderThanWindow_DoNotCountAgainstHealth()
    {
        var time = new FakeTimeProvider(Start);
        var monitor = new TickHealthMonitor();
        monitor.RecordTick(time.GetUtcNow());

        // 100 failures, then 100 successes: the window holds only the last 100, all
        // successes, so the failure rate is 0%.
        for (var i = 0; i < 100; i++)
        {
            monitor.RecordFireOutcome(success: false);
        }

        for (var i = 0; i < 100; i++)
        {
            monitor.RecordFireOutcome(success: true);
        }

        var check = NewCheck(monitor, faulted: false, time);

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    }

    /// <summary>
    /// Verifies a faulted scheduler is reported unhealthy even when the tick is recent
    /// and dispatches have all succeeded.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Faulted_IsUnhealthy()
    {
        var time = new FakeTimeProvider(Start);
        var monitor = new TickHealthMonitor();
        monitor.RecordTick(time.GetUtcNow());
        monitor.RecordFireOutcome(success: true);

        var check = NewCheck(monitor, faulted: true, time);

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    }

    private static SchedulerHealthCheck NewCheck(
        ITickHealthMonitor monitor,
        bool faulted,
        TimeProvider timeProvider)
        => new(monitor, new StubFaultSource(faulted), timeProvider, ExpectedInterval);

    private sealed class StubFaultSource : ISchedulerFaultSource
    {
        public StubFaultSource(bool isFaulted) => this.IsFaulted = isFaulted;

        public bool IsFaulted { get; }
    }
}
