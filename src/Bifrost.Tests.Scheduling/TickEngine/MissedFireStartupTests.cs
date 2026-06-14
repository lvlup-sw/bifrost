// =============================================================================
// <copyright file="MissedFireStartupTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.TickEngine;

/// <summary>
/// Tests for the <see cref="ScheduleTickLoop"/>'s startup missed-fire reconciliation
/// (Task 26, DR-3/DR-7/DR-10): on startup the loop loads each job's durable
/// <c>LastFiredAt</c> from the store and applies its missed-fire policy before
/// resuming the normal schedule.
/// </summary>
/// <remarks>
/// Seeding model under test: a job is registered through the registry (so a live
/// dispatcher is attached) and its durable <c>LastFiredAt</c> is pre-seeded into the
/// store to simulate fires that happened before a restart. The loop seeds from the
/// store's record (durable timing state) and resolves the dispatcher from the
/// registry.
/// </remarks>
[ParallelLimiter<TickEngineParallelLimit>]
public sealed class MissedFireStartupTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies <see cref="MissedFirePolicy.Coalesce"/> collapses a 5-occurrence
    /// backlog into exactly one catch-up dispatch, then schedules the next fire.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Coalesce_FivePastIntervals_FiresOnceThenNormal()
    {
        var dispatcher = new CountingDispatcher();
        await using var fx = await Fixture.StartWithSeededJobAsync(
            "report",
            Cadence.Interval(TimeSpan.FromMinutes(1)),
            MissedFirePolicy.Coalesce,
            lastFiredAt: Start.AddMinutes(-5),
            dispatcher).ConfigureAwait(false);

        await Assert.That(dispatcher.FireCount).IsEqualTo(1);

        // Next fire is one interval out from now.
        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(dispatcher.FireCount).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies <see cref="MissedFirePolicy.FireAllMissed"/> replays each missed
    /// occurrence, then resumes the normal schedule.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FireAllMissed_FivePastIntervals_FiresFiveThenNormal()
    {
        var dispatcher = new CountingDispatcher();
        await using var fx = await Fixture.StartWithSeededJobAsync(
            "report",
            Cadence.Interval(TimeSpan.FromMinutes(1)),
            MissedFirePolicy.FireAllMissed,
            lastFiredAt: Start.AddMinutes(-5),
            dispatcher).ConfigureAwait(false);

        await Assert.That(dispatcher.FireCount).IsEqualTo(5);

        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(dispatcher.FireCount).IsEqualTo(6);
    }

    /// <summary>
    /// Verifies <see cref="MissedFirePolicy.SkipMissed"/> discards the backlog and
    /// resumes from the next future occurrence.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SkipMissed_FivePastIntervals_FiresNoneThenNormal()
    {
        var dispatcher = new CountingDispatcher();
        await using var fx = await Fixture.StartWithSeededJobAsync(
            "report",
            Cadence.Interval(TimeSpan.FromMinutes(1)),
            MissedFirePolicy.SkipMissed,
            lastFiredAt: Start.AddMinutes(-5),
            dispatcher).ConfigureAwait(false);

        await Assert.That(dispatcher.FireCount).IsEqualTo(0);

        fx.Time.Advance(TimeSpan.FromMinutes(1));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(dispatcher.FireCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies a missed backlog exceeding the catch-up cap is capped at 100 under
    /// <see cref="MissedFirePolicy.FireAllMissed"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FireAllMissed_ExceedingCap_CapsAt100()
    {
        var dispatcher = new CountingDispatcher();
        await using var fx = await Fixture.StartWithSeededJobAsync(
            "report",
            Cadence.Interval(TimeSpan.FromMinutes(1)),
            MissedFirePolicy.FireAllMissed,
            lastFiredAt: Start.AddMinutes(-500),
            dispatcher).ConfigureAwait(false);

        await Assert.That(dispatcher.FireCount).IsEqualTo(100);

        // A JobMissedFireEvent reports the capped count and the policy.
        var missed = fx.Events.OfType<JobMissedFireEvent>().Single();
        await Assert.That(missed.MissedCount).IsEqualTo(100);
        await Assert.That(missed.Policy).IsEqualTo(MissedFirePolicy.FireAllMissed);
    }

    /// <summary>
    /// Verifies a job with no missed occurrences fires nothing on startup and follows
    /// its normal schedule.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task NoMissed_FiresNoneThenNormal()
    {
        var dispatcher = new CountingDispatcher();
        await using var fx = await Fixture.StartWithSeededJobAsync(
            "report",
            Cadence.Interval(TimeSpan.FromMinutes(5)),
            MissedFirePolicy.Coalesce,
            lastFiredAt: Start.AddMinutes(-1),
            dispatcher).ConfigureAwait(false);

        await Assert.That(dispatcher.FireCount).IsEqualTo(0);

        fx.Time.Advance(TimeSpan.FromMinutes(5));
        await fx.Loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
        await Assert.That(dispatcher.FireCount).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies a coalesced catch-up publishes a <see cref="JobMissedFireEvent"/> that
    /// reports the actual missed count and the applied policy.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Coalesce_PublishesMissedFireEvent_WithCountAndPolicy()
    {
        var dispatcher = new CountingDispatcher();
        await using var fx = await Fixture.StartWithSeededJobAsync(
            "report",
            Cadence.Interval(TimeSpan.FromMinutes(1)),
            MissedFirePolicy.Coalesce,
            lastFiredAt: Start.AddMinutes(-5),
            dispatcher).ConfigureAwait(false);

        var missed = fx.Events.OfType<JobMissedFireEvent>().Single();
        await Assert.That(missed.JobName).IsEqualTo("report");
        await Assert.That(missed.MissedCount).IsEqualTo(5);
        await Assert.That(missed.Policy).IsEqualTo(MissedFirePolicy.Coalesce);
    }

    /// <summary>
    /// A test harness owning a started <see cref="ScheduleTickLoop"/> seeded with a
    /// single job whose durable <c>LastFiredAt</c> is pre-set in the store.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly RecordingSchedulerEventSink sink;

        private Fixture(FakeTimeProvider time, ScheduleTickLoop loop, RecordingSchedulerEventSink sink)
        {
            this.Time = time;
            this.Loop = loop;
            this.sink = sink;
        }

        public FakeTimeProvider Time { get; }

        public ScheduleTickLoop Loop { get; }

        public IReadOnlyList<object> Events => this.sink.Published;

        public static async Task<Fixture> StartWithSeededJobAsync(
            string name,
            Cadence cadence,
            MissedFirePolicy policy,
            DateTimeOffset lastFiredAt,
            IJobDispatcher dispatcher)
        {
            var time = new FakeTimeProvider(Start);
            var store = new InMemoryScheduleStore();
            var registry = new ScheduleRegistry(store, time);

            // Register so a live dispatcher is attached and a record exists.
            await registry.RegisterAsync(name, cadence, policy, dispatcher).ConfigureAwait(false);

            // Pre-seed durable LastFiredAt into the store to simulate fires that
            // happened before this process started. The next fire recorded here is
            // ignored by the loop, which recomputes it from the cadence.
            await store.SaveAsync(
                new JobRecord(
                    name, cadence, policy, JobState.Running, lastFiredAt, NextFireAt: null,
                    DispatchKind: "custom", DispatcherTypeName: dispatcher.GetType().FullName,
                    Metadata: new Dictionary<string, string>()),
                CancellationToken.None).ConfigureAwait(false);

            var sink = new RecordingSchedulerEventSink();
            var router = new JobDispatcherRouter(sink);
            var loop = new ScheduleTickLoop(
                registry, store, time, router, sink,
                NullLogger<ScheduleTickLoop>.Instance, new SchedulerOptions());

            var fx = new Fixture(time, loop, sink);
            await ((IHostedService)loop).StartAsync(CancellationToken.None).ConfigureAwait(false);
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);
            return fx;
        }

        public async ValueTask DisposeAsync()
        {
            await ((IHostedService)this.Loop).StopAsync(CancellationToken.None).ConfigureAwait(false);
            this.Loop.Dispose();
        }
    }

    /// <summary>
    /// A dispatcher that counts fires. Thread-safe: the router dispatches on the pool.
    /// </summary>
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
