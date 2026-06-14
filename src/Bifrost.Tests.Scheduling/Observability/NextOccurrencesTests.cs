// =============================================================================
// <copyright file="NextOccurrencesTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Observability;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.Testing;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.Observability;

/// <summary>
/// Tests for <see cref="IBifrostScheduleInspector.GetNextOccurrences"/> (Task 51,
/// DR-8/R10): the preview returns N distinct, ordered future instants, and the same
/// cadence engine is used for previewing and for actual firing, so the instants are
/// identical (coravel#250, Hangfire#899).
/// </summary>
[ParallelLimiter<TickEngine.TickEngineParallelLimit>]
public sealed class NextOccurrencesTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that <see cref="IBifrostScheduleInspector.GetNextOccurrences"/> returns
    /// exactly <c>count</c> distinct, ordered future instants for an interval cadence.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GetNextOccurrences_ReturnsNFutureInstants_Ordered()
    {
        var fakeTime = new FakeTimeProvider(Start);
        var registry = new ScheduleRegistry(new InMemoryScheduleStore(), fakeTime);
        IBifrostScheduleInspector inspector = new ScheduleInspector(registry);

        await registry.RegisterAsync(
                "interval-job",
                Cadence.Interval(TimeSpan.FromMinutes(5)),
                MissedFirePolicy.Coalesce,
                new NoopDispatcher())
            .ConfigureAwait(false);

        var occurrences = inspector.GetNextOccurrences("interval-job", count: 3);

        await Assert.That(occurrences).HasCount(3);
        // All occurrences must be in the future.
        foreach (var occurrence in occurrences)
        {
            await Assert.That(occurrence).IsGreaterThan(Start);
        }

        // All occurrences must be strictly ordered (ascending).
        for (int i = 1; i < occurrences.Count; i++)
        {
            await Assert.That(occurrences[i]).IsGreaterThan(occurrences[i - 1]);
        }

        // First occurrence is Start + 5min, second is Start + 10min, etc.
        await Assert.That(occurrences[0]).IsEqualTo(Start.AddMinutes(5));
        await Assert.That(occurrences[1]).IsEqualTo(Start.AddMinutes(10));
        await Assert.That(occurrences[2]).IsEqualTo(Start.AddMinutes(15));
    }

    /// <summary>
    /// Verifies that the instants returned by
    /// <see cref="IBifrostScheduleInspector.GetNextOccurrences"/> are EXACTLY the
    /// instants the tick engine fires for (same cadence engine for preview and firing —
    /// coravel#250, Hangfire#899).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GetNextOccurrences_MatchesActualFires()
    {
        var fakeTime = new FakeTimeProvider(Start);
        var store = new InMemoryScheduleStore();
        var registry = new ScheduleRegistry(store, fakeTime);
        var sink = new NullSchedulerEventSink();
        var router = new JobDispatcherRouter(sink);
        var loop = new ScheduleTickLoop(
            registry,
            store,
            fakeTime,
            router,
            sink,
            NullLogger<ScheduleTickLoop>.Instance,
            new SchedulerOptions());

        await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
        await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

        IBifrostScheduleInspector inspector = new ScheduleInspector(registry);
        var dispatcher = new TimestampingDispatcher();

        try
        {
            await registry.RegisterAsync(
                    "match-job",
                    Cadence.Interval(TimeSpan.FromMinutes(5)),
                    MissedFirePolicy.Coalesce,
                    dispatcher)
                .ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // Preview the next 3 occurrences BEFORE advancing time.
            var previewed = inspector.GetNextOccurrences("match-job", count: 3);
            await Assert.That(previewed).HasCount(3);

            // Advance through all 3 intervals using the harness.
            var harness = new SchedulerTestHarness(loop, fakeTime);
            await harness.AdvanceAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            await harness.AdvanceAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            await harness.AdvanceAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            // The actual fire times must equal the previewed instants exactly.
            await Assert.That(dispatcher.FireTimes).HasCount(3);
            for (int i = 0; i < 3; i++)
            {
                await Assert.That(dispatcher.FireTimes[i]).IsEqualTo(previewed[i]);
            }
        }
        finally
        {
            await ((IHostedService)loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            loop.Dispose();
        }
    }

    private sealed class NoopDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class TimestampingDispatcher : IJobDispatcher
    {
        private readonly List<DateTimeOffset> fireTimes = [];

        public IReadOnlyList<DateTimeOffset> FireTimes
        {
            get
            {
                lock (this.fireTimes)
                {
                    return [.. this.fireTimes];
                }
            }
        }

        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
        {
            lock (this.fireTimes)
            {
                this.fireTimes.Add(context.FireTime);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullSchedulerEventSink : ISchedulerEventSink
    {
        public void Publish<TEvent>(in TEvent evt) where TEvent : struct { }
    }
}
