// =============================================================================
// <copyright file="SaturationTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.TickEngine;

/// <summary>
/// Saturation test for the <see cref="ScheduleTickLoop"/> (Task 48, DR-10,
/// Hangfire#751): when 1,000+ jobs are all due at the same instant, the tick
/// loop must drain the entire heap entry set — no job may be silently lost or
/// carried past the loop iteration.
/// </summary>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class SaturationTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    // A real root provider so each fire opens (and disposes) a genuine per-fire scope
    // (F2/M2) — exercises the per-fire scope cost the saturation path now incurs.
    private static readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(30);

    private const int JobCount = 1_000;

    /// <summary>
    /// Verifies that 1,000+ jobs all due at the same instant are all dispatched (or
    /// carried into immediately-following iterations) with no losses (Hangfire#751).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Saturation_1000PlusJobsDueSameInstant_AllDispatched_NoneLost()
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, time);
        var sink = new RecordingSchedulerEventSink();
        var router = new JobDispatcherRouter(sink);
        var loop = new ScheduleTickLoop(
            registry, store, time, router, sink,
            NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions(),
            serviceProvider: Services);

        await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
        await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // Register JobCount one-shot jobs all due at the same instant (Start + 1 min).
        var dispatchers = new CountingDispatcher[JobCount];
        var fireAt = Start.AddMinutes(1);
        for (var i = 0; i < JobCount; i++)
        {
            dispatchers[i] = new CountingDispatcher();
            var name = $"job-{i:D4}";
            await registry.RegisterAsync(name, Cadence.At(fireAt), MissedFirePolicy.Coalesce, dispatchers[i])
                .ConfigureAwait(false);
        }

        // Let the loop absorb all registration commands.
        await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        // Advance to the fire instant — all 1,000 jobs become due simultaneously.
        time.Advance(TimeSpan.FromMinutes(1));

        // Allow enough wait cycles for all jobs to be dispatched. The loop drains
        // the heap in DispatchDueJobs; if any entries are left they must be picked
        // up in the immediately-following iteration (the re-arm delay is zero once
        // NextFireAt ≤ now).
        await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
        loop.Dispose();

        // Every job must have fired exactly once.
        var totalFires = dispatchers.Sum(d => d.FireCount);
        await Assert.That(totalFires).IsEqualTo(JobCount);

        for (var i = 0; i < JobCount; i++)
        {
            await Assert.That(dispatchers[i].FireCount).IsEqualTo(1);
        }
    }

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
