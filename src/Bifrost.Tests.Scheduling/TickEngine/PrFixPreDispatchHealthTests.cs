// =============================================================================
// <copyright file="PrFixPreDispatchHealthTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Observability;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.TickEngine;

/// <summary>
/// Regression tests for PR #25 CodeRabbit FIX B1: a fire that fails BEFORE the
/// in-flight tracking dispatcher runs — the per-fire <see cref="IServiceScope"/>
/// fails to create — must record the failed-fire outcome in the same
/// <see cref="ITickHealthMonitor"/> the normal/post-dispatch path uses, so
/// <see cref="ITickHealthMonitor.FailureRate"/> / <see cref="ITickHealthMonitor.RecentFireCount"/>
/// do not under-report a pre-dispatch fire failure.
/// </summary>
/// <remarks>
/// The post-dispatch path records the outcome from <c>InFlightTrackingDispatcher</c>
/// (success or a throwing dispatcher). Before this fix the two pre-dispatch failure
/// paths in <c>ScheduleTickLoop.Dispatch</c> (no resolvable dispatcher; per-fire
/// scope-creation fault) published a <see cref="JobFireFailedEvent"/> and recorded a
/// metric but never recorded the fire outcome in the health monitor — so a scheduler
/// whose every fire fails pre-dispatch reported a 0% failure rate.
/// </remarks>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class PrFixPreDispatchHealthTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that when the per-fire scope creation faults (a pre-dispatch failure),
    /// the failed fire is recorded into the health monitor: the recent-fire window is
    /// non-empty and the failure rate is 1.0. Without the fix the monitor never sees
    /// the failure and both stay at zero, so the failure rate under-reports.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ScopeCreationFault_RecordsFailedFireInHealthMonitor()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        var sink = new RecordingSchedulerEventSink();
        var router = new JobDispatcherRouter(sink);
        var monitor = new CountingHealthMonitor();
        var loop = new ScheduleTickLoop(
            registry, store, time, router, sink,
            NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions(),
            healthMonitor: monitor,
            serviceProvider: new ThrowingScopeProvider());

        try
        {
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            await registry.RegisterAsync(
                "job", Cadence.Interval(TimeSpan.FromMinutes(1)), MissedFirePolicy.Coalesce,
                new CountingDispatcher()).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // Make the job due; the per-fire scope creation faults BEFORE the in-flight
            // tracking dispatcher (and its health-monitor recording) ever runs.
            time.Advance(TimeSpan.FromMinutes(1));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // The compensating failed-fire event was published (existing behaviour) AND
            // the failed fire was recorded into the health monitor (the fix).
            await Assert.That(sink.Published.OfType<JobFireFailedEvent>().Count()).IsEqualTo(1);
            await Assert.That(monitor.RecentFireCount).IsEqualTo(1);
            await Assert.That(monitor.FailureRate).IsEqualTo(1d);
            await Assert.That(monitor.SuccessCount).IsEqualTo(0);
            await Assert.That(monitor.FailureCount).IsEqualTo(1);
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
        }
    }

    /// <summary>
    /// An <see cref="ITickHealthMonitor"/> that counts recorded outcomes so a test can
    /// assert a pre-dispatch failure reaches the monitor. Delegates rate/count semantics
    /// to a simple tally rather than the production ring buffer.
    /// </summary>
    private sealed class CountingHealthMonitor : ITickHealthMonitor
    {
        private int successCount;
        private int failureCount;

        public int SuccessCount => Volatile.Read(ref this.successCount);

        public int FailureCount => Volatile.Read(ref this.failureCount);

        public DateTimeOffset? LastTickAt { get; private set; }

        public double FailureRate
        {
            get
            {
                var total = this.SuccessCount + this.FailureCount;
                return total == 0 ? 0d : (double)this.FailureCount / total;
            }
        }

        public int RecentFireCount => this.SuccessCount + this.FailureCount;

        public void RecordTick(DateTimeOffset at) => this.LastTickAt = at;

        public void RecordFireOutcome(bool success)
        {
            if (success)
            {
                Interlocked.Increment(ref this.successCount);
            }
            else
            {
                Interlocked.Increment(ref this.failureCount);
            }
        }
    }

    /// <summary>
    /// An <see cref="IServiceProvider"/> that hands back itself as the scope factory and
    /// always throws when a scope is requested — simulating a root provider disposed
    /// while a fire is in progress (the pre-dispatch scope-creation fault path).
    /// </summary>
    private sealed class ThrowingScopeProvider : IServiceProvider, IServiceScopeFactory
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceScopeFactory) ? this : null;

        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("Per-fire scope creation failed (test).");
    }

    /// <summary>A dispatcher that counts how many times it ran.</summary>
    private sealed class CountingDispatcher : IJobDispatcher
    {
        private int fireCount;

        public int FireCount => Volatile.Read(ref this.fireCount);

        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref this.fireCount);
            return ValueTask.CompletedTask;
        }
    }
}
